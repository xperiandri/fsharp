// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Navigate To sorts a match from the file the user is editing before the same name found elsewhere - the
/// platform does the sorting itself, from `FSharpNavigateToSearchResult.SecondarySort`.
module FSharp.Editor.Tests.NavigateToSearchActiveFileTests

open System
open System.Collections.Immutable
open System.Threading

open Xunit

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.NavigateTo
open Microsoft.VisualStudio.FSharp.Editor

open FSharp.Editor.Tests.Helpers

/// One export provider per test: the active document it tracks is shared within it, and a test must not
/// see the focus another test left behind.
let private searchServices () =
    let provider = MefHelpers.createExportProvider ()
    let service: IFSharpNavigateToSearchService = provider.GetExportedValue()
    let tracker: FSharpActiveDocumentTracker = provider.GetExportedValue()
    service, tracker

/// Two documents that each declare a value of the same name, so a search for it matches both equally -
/// the only thing that can still tell the results apart is which file the user is editing.
let private twoDocumentProject () =
    let projectId = ProjectId.CreateNewId()

    let documents =
        [ "Active"; "Other" ]
        |> List.map (fun name -> RoslynTestHelpers.CreateDocumentInfo projectId $"C:\\{name}.fs" "let target = 1\n")

    let projectInfo =
        RoslynTestHelpers.CreateProjectInfo projectId "C:\\test.fsproj" documents

    let solution = RoslynTestHelpers.CreateSolution [ projectInfo ]

    { RoslynTestHelpers.DefaultProjectOptions with
        SourceFiles = [| "C:\\Active.fs"; "C:\\Other.fs" |]
    }
    |> RoslynTestHelpers.SetProjectOptions projectId solution

    solution.Projects |> Seq.exactlyOne

let private secondarySortOf (service: IFSharpNavigateToSearchService) (project: Project) (documentName: string) =
    let results =
        service.SearchProjectAsync(project, ImmutableArray.Empty, "target", service.KindsProvided, CancellationToken.None).Result

    results
    |> Seq.find (fun result -> result.NavigableItem.Document.FilePath.EndsWith($"{documentName}.fs", StringComparison.Ordinal))
    |> _.SecondarySort

[<Fact>]
let ``a match in the active file sorts before the same name elsewhere`` () =
    let service, tracker = searchServices ()
    let project = twoDocumentProject ()

    tracker.SetFocus
        {
            FilePath = "C:\\Active.fs"
            FirstLine = 1
            LastLine = 1
        }

    let sortOf = secondarySortOf service project
    Assert.True(String.CompareOrdinal(sortOf "Active", sortOf "Other") < 0)

[<Fact>]
let ``with no active file, the same name sorts alike everywhere`` () =
    let service, _ = searchServices ()
    let project = twoDocumentProject ()

    let sortOf = secondarySortOf service project
    Assert.Equal(sortOf "Active", sortOf "Other")
