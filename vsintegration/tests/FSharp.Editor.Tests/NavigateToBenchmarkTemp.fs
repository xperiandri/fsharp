namespace FSharp.Editor.Tests

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.NavigateTo
open FSharp.Editor.Tests.Helpers

// Temporary, not committed: times Navigate To searches over many projects and documents.
module NavigateToBenchmarkTemp =

    let projectCount = 20
    let documentsPerProject = 100

    let median (xs: float list) =
        let sorted = List.sort xs
        sorted[sorted.Length / 2]

    [<Fact>]
    let ``bench navigate to`` () =
        let label = Environment.GetEnvironmentVariable "NAVTO_BENCH_LABEL" |> fun l -> if isNull l then "unlabelled" else l
        let provider = MefHelpers.createExportProvider ()
        let service: IFSharpNavigateToSearchService = provider.GetExportedValue()
        let projectIds = [ for _ in 1..projectCount -> ProjectId.CreateNewId() ]

        let pathOf i d = $"C:\\P{i}\\File{d}.fs"

        let projectInfos =
            [
                for i, projectId in List.indexed projectIds ->
                    let documents =
                        [
                            for d in 1..documentsPerProject ->
                                RoslynTestHelpers.CreateDocumentInfo
                                    projectId
                                    (pathOf i d)
                                    $"module P{i}F{d}\n\nlet value{d} = {d}\n\ntype T{d}() = class end\n"
                        ]

                    RoslynTestHelpers.CreateProjectInfo projectId $"C:\\P{i}\\P{i}.fsproj" documents
            ]

        let solution = RoslynTestHelpers.CreateSolution projectInfos

        for i, projectId in List.indexed projectIds do
            { RoslynTestHelpers.DefaultProjectOptions with
                ProjectFileName = $"C:\\P{i}\\P{i}.fsproj"
                SourceFiles = [| for d in 1..documentsPerProject -> pathOf i d |]
            }
            |> RoslynTestHelpers.SetProjectOptions projectId solution

        let search (solution: Solution) =
            let mutable found = 0

            for projectId in projectIds do
                let project = solution.GetProject projectId

                found <-
                    found
                    + service
                        .SearchProjectAsync(project, ImmutableArray.Empty, "zzzqqq", service.KindsProvided, CancellationToken.None)
                        .Result
                        .Length

            found

        let time f =
            let sw = Stopwatch.StartNew()
            f () |> ignore
            sw.Elapsed.TotalMilliseconds

        let lines = ResizeArray<string>()
        let log (text: string) = lines.Add text

        let cold = time (fun () -> search solution)
        log $"[{label}] documents: {projectCount * documentsPerProject}; cold first search: {cold:F0} ms"

        let warm = [ for _ in 1..40 -> time (fun () -> search solution) ] |> List.skip 5
        log $"[{label}] warm unchanged solution: min {List.min warm:F1} ms, median {median warm:F1} ms, mean {List.average warm:F1} ms"

        // A new Solution instance has new Project objects for every project, as after any edit in the editor.
        let afterEdit =
            [
                for k in 0..9 ->
                    let target = (solution.GetProject projectIds[k]).Documents |> Seq.head

                    let edited =
                        solution.WithDocumentText(target.Id, SourceText.From($"module Edited{k}\n\nlet edited{k} = {k}\n"))

                    let first = time (fun () -> search edited)
                    let again = time (fun () -> search edited)
                    first, again
            ]

        log
            $"[{label}] first search after an edit: median {median (List.map fst afterEdit):F1} ms (min {List.min (List.map fst afterEdit):F1}); the repeat: median {median (List.map snd afterEdit):F1} ms"

        File.WriteAllLines(Path.Combine(@"C:\w", $"navto-bench-{label}.txt"), lines)
