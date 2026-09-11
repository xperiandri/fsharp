// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Composition
open System.Diagnostics
open System.IO
open System.Threading.Tasks

open Microsoft.CodeAnalysis

/// Lays out the data of a solution as Roslyn's `DefaultPersistentStorageConfiguration` lays out its database: one
/// directory per process and solution under the user's local application data.
[<Export(typeof<IFSharpPersistentStorageConfiguration>); Export; Shared>]
type internal FSharpPersistentStorageConfiguration [<ImportingConstructor>] () =

    static let invalidPathChars = set [ yield! Path.GetInvalidPathChars(); '/' ]

    static let safeName (fullPath: string) =
        let fileName = Path.GetFileName fullPath

        let prefix =
            if fileName.Length > 20 then
                fileName.Substring(0, 20)
            else
                fileName

        match
            $"{prefix}-{FSharpChecksum.Create fullPath}"
            |> String.filter (invalidPathChars.Contains >> not)
        with
        | name when String.IsNullOrWhiteSpace name -> "None"
        | name -> name

    static let moduleFileName = safeName (Process.GetCurrentProcess().MainModule.FileName)

    /// Set by tests, which must not write where Visual Studio reads.
    member val CacheDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Microsoft",
            "VisualStudio",
            "FSharp",
            "Cache"
        ) with get, set

    interface IFSharpPersistentStorageConfiguration with
        member this.TryGetStorageLocation(solution) =
            match solution.FilePath with
            | null -> ValueNone
            | path when not (Path.IsPathRooted path) -> ValueNone
            | path -> ValueSome(Path.Combine(this.CacheDirectory, moduleFileName, safeName path, "files", "v1"))

/// One file per document and name, named by a hash of the project's and the document's paths and names — never by
/// the document's id, which lasts one session. The file starts with the checksum it was written with.
type private FilePersistentStorage(directory: string) =

    static let magic = "FSPS"B

    static let header (checksum: FSharpChecksum) = Array.append magic (checksum.ToBytes())

    let pathOf (document: Document) name =
        let project = document.Project

        let key =
            FSharpChecksum.Create [ project.FilePath; project.Name; document.FilePath; document.Name; name ]

        Path.Combine(directory, BitConverter.ToString(key.ToBytes()).Replace("-", ""))

    interface IFSharpChecksummedPersistentStorage with
        member _.ReadStreamAsync(document, name, checksum, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            let path = pathOf document name
            let expected = header checksum

            match File.Exists path with
            | false -> Task.FromResult null
            | true ->
                match File.ReadAllBytes path with
                | bytes when bytes.Length >= expected.Length && Array.sub bytes 0 expected.Length = expected ->
                    Task.FromResult(new MemoryStream(bytes, expected.Length, bytes.Length - expected.Length, false) :> Stream)
                | _ -> Task.FromResult null

        member _.WriteStreamAsync(document, name, stream, checksum, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            Directory.CreateDirectory directory |> ignore
            let path = pathOf document name
            let written = $"{path}.{Guid.NewGuid():N}.tmp"

            try
                do
                    use file =
                        new FileStream(written, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                    let header = header checksum
                    file.Write(header, 0, header.Length)
                    stream.CopyTo file

                // Another writer of the same entry that gets there first makes this one fail, and its data stands.
                if File.Exists path then
                    File.Replace(written, path, null)
                else
                    File.Move(written, path)

                Task.FromResult true
            finally
                File.Delete written

/// The storage of the solution it was last asked for, as Roslyn keeps one database open.
[<Export(typeof<IFSharpChecksummedPersistentStorageService>); Shared>]
type internal FSharpFilePersistentStorageService [<ImportingConstructor>] (configuration: IFSharpPersistentStorageConfiguration) =

    static let noStorage =
        { new IFSharpChecksummedPersistentStorage with
            member _.ReadStreamAsync(_, _, _, _) = Task.FromResult null
            member _.WriteStreamAsync(_, _, _, _, _) = Task.FromResult false
        }

    let mutable current = (null: string), noStorage

    let storageOf (solution: Solution) =
        match current with
        | solutionPath, storage when String.Equals(solutionPath, solution.FilePath, StringComparison.Ordinal) -> storage
        | _ ->
            let storage =
                match configuration.TryGetStorageLocation solution with
                | ValueSome directory -> FilePersistentStorage directory :> IFSharpChecksummedPersistentStorage
                | ValueNone -> noStorage

            current <- solution.FilePath, storage
            storage

    interface IFSharpChecksummedPersistentStorageService with
        member _.GetStorageAsync(solution, _cancellationToken) = Task.FromResult(storageOf solution)
