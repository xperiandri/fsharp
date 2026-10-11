// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Editor.Tests

open Xunit
open System.Collections.Concurrent
open System.Collections.Immutable
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text
open Microsoft.VisualStudio.FSharp.Editor
open FSharp.Compiler.Text
open FSharp.Editor.Tests.Helpers
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.NavigateTo

module NavigateToSearchServiceTests =

    let fileContents =
        """
module HeyHo =
    let inline (+>) f c = f c

    let Żółwik żwawy = ()

    let ``a few words`` = 0

    let one' = 1
    let two'' = 2

    type Widget =
        { Size: int }

        static member Create size = { Size = size }

    type CamelCaseLongName = class end

    module Alpha =
        module Beta =
            type Gamma() =
                member val Delta = 3
"""

    let sourceText = SourceText.From(fileContents)

    let solution = RoslynTestHelpers.CreateSolution(fileContents)
    let project = solution.Projects |> Seq.exactlyOne
    let provider = MefHelpers.createExportProvider ()

    let navigateToSearch pattern =
        let service: IFSharpNavigateToSearchService = provider.GetExportedValue()

        service.SearchProjectAsync(project, [] |> Seq.toImmutableArray, pattern, service.KindsProvided, CancellationToken.None).Result

    let assertResultsContain pattern expected =
        navigateToSearch pattern
        |> Seq.exists (fun i -> i.Name = expected)
        |> Assert.True

    [<Fact>]
    let ``unicode symbols`` () = assertResultsContain "Żó" "Żółwik"

    [<Fact>]
    let ``capitalized camel case`` () =
        assertResultsContain "CLN" "CamelCaseLongName"

    [<Fact>]
    let ``lower camel case`` () =
        assertResultsContain "cln" "CamelCaseLongName"

    [<Fact>]
    let ``substring`` () = assertResultsContain "ne'" "one'"

    [<Fact>]
    let ``backticked identifier`` () =
        assertResultsContain "a few words" "a few words"

    [<Fact>]
    let ``operator`` () = assertResultsContain "+>" "+>"

    [<Fact>]
    let ``a static member of a record`` () = assertResultsContain "Create" "Create"

    [<Fact>]
    let ``nested containers`` () =
        assertResultsContain "hh.a.b.g.d" "Delta"

    [<Fact>]
    let ``results carry the counts Navigate To sorts equal matches by`` () =
        let result = navigateToSearch "+>" |> Seq.find (fun i -> i.Name = "+>")
        Assert.Equal((2, 0), (result.ParameterCount, result.TypeParameterCount))

    /// The text the results window highlights in a result's name.
    let matchedTextOf pattern expected =
        let result = navigateToSearch pattern |> Seq.find (fun i -> i.Name = expected)

        [
            for span in result.NameMatchSpans -> result.Name.Substring(span.Start, span.Length)
        ]

    [<Fact>]
    let ``the highlighted spans are the camel case humps that matched`` () =
        Assert.Equal<string list>([ "C"; "L"; "N" ], matchedTextOf "CLN" "CamelCaseLongName")

    [<Fact>]
    let ``the highlighted spans are offsets in the name, not in the candidate the matcher saw`` () =
        Assert.Equal<string list>([ "Delta" ], matchedTextOf "Delta" "Delta")

    [<Fact>]
    let ``a backticked identifier highlights the matched substring`` () =
        Assert.Equal<string list>([ "few" ], matchedTextOf "few" "a few words")

    /// Hands a document's text over once the task `onLoad` returns for its path is done.
    type private GatedTextLoader(path: string, source: string, onLoad: string -> Task) =
        inherit TextLoader()

        override _.LoadTextAndVersionAsync(_: LoadTextOptions, _: CancellationToken) =
            task {
                do! onLoad path
                return TextAndVersion.Create(SourceText.From source, VersionStamp.Create(), path)
            }

    /// The priority document hands its text over only once the other one has been asked for its own, so the
    /// search ends only if the rest of the project is reached while a priority document is still being read.
    [<Fact>]
    let ``a project's priority documents are reached first and do not hold back the rest`` () =
        let first = "C:\\First.fs"
        let second = "C:\\Second.fs"
        let reached = ConcurrentQueue<string>()

        let firstReached =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let onLoad path : Task =
            reached.Enqueue path

            if path = first then
                firstReached.TrySetResult() |> ignore

            firstReached.Task

        let projectId = ProjectId.CreateNewId()

        let documentOf path source =
            DocumentInfo.Create(DocumentId.CreateNewId projectId, path, loader = GatedTextLoader(path, source, onLoad), filePath = path)

        let solution =
            [
                documentOf first "module First\n\nlet shared = 1\n"
                documentOf second "module Second\n\nlet shared = 2\n"
            ]
            |> RoslynTestHelpers.CreateProjectInfo projectId "C:\\test.fsproj"
            |> List.singleton
            |> RoslynTestHelpers.CreateSolution

        { RoslynTestHelpers.DefaultProjectOptions with
            SourceFiles = [| first; second |]
        }
        |> RoslynTestHelpers.SetProjectOptions projectId solution

        let project = solution.GetProject projectId

        let priority =
            project.Documents |> Seq.find (fun document -> document.FilePath = second)

        let service: IFSharpNavigateToSearchService = provider.GetExportedValue()

        let search =
            service.SearchProjectAsync(project, ImmutableArray.Create priority, "shared", service.KindsProvided, CancellationToken.None)

        Assert.True(search.Wait(System.TimeSpan.FromMinutes 1.0), "The rest of the project waited for the priority document.")
        Assert.Equal(second, reached |> Seq.head)

    /// A standalone script has its options kept per document, so its project never has any to key the script's
    /// parse by. A parse that depends on the defines is kept under the document itself instead.
    [<Theory>]
    [<InlineData("let standaloneValue = 1\n")>]
    [<InlineData("#if FOO\nlet fooOnly = 0\n#endif\nlet standaloneValue = 1\n")>]
    let ``a repeated search of an unchanged standalone script does not parse it again`` (script: string) =
        let path = "C:\\Standalone.fsx"
        let projectId = ProjectId.CreateNewId()

        let solution =
            [ RoslynTestHelpers.CreateDocumentInfo projectId path script ]
            |> RoslynTestHelpers.CreateProjectInfo projectId FSharpConstants.FSharpMiscellaneousFilesName
            |> List.singleton
            |> RoslynTestHelpers.CreateSolution

        let parseRequests = ref 0

        use listener =
            new ActivityListener(
                ShouldListenTo = (fun source -> source.Name = FSharp.Compiler.Diagnostics.ActivityNames.FscSourceName),
                Sample = (fun _ -> ActivitySamplingResult.AllData),
                ActivityStarted =
                    (fun activity ->
                        if
                            activity.OperationName = "BackgroundCompiler.ParseFile"
                            && activity.Tags |> Seq.exists (fun tag -> tag.Value = path)
                        then
                            Interlocked.Increment(&parseRequests.contents) |> ignore)
            )

        ActivitySource.AddActivityListener listener

        let service: IFSharpNavigateToSearchService = provider.GetExportedValue()
        let project = solution.GetProject projectId

        let search () =
            service
                .SearchProjectAsync(project, ImmutableArray.Empty, "standaloneValue", service.KindsProvided, CancellationToken.None)
                .Result
            |> Seq.map _.Name
            |> Seq.toList

        Assert.Equal<string list>([ "standaloneValue" ], search ())
        Assert.Equal<string list>([ "standaloneValue" ], search ())
        Assert.Equal(1, parseRequests.Value)
