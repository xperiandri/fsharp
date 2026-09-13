// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Composition
open System.Threading.Tasks

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.ExternalAccess.FSharp
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.Classification
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.FindUsages
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.Editor.FindUsages
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text

open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open CancellableTasks

module FSharpFindUsagesService =

    /// Reports the uses found in one document: its text is read once for all of them, and Roslyn takes
    /// them one at a time anyway.
    let onSymbolFound
        declarationRange
        externalDefinitionItem
        definitionItems
        isExternal
        symbolName
        (onReferenceFoundAsync: FSharpSourceReferenceItem -> Task)
        (doc: Document)
        (symbolUses: range seq)
        =
        cancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()
            let! sourceText = doc.GetTextAsync(cancellationToken)
            let classifier = FSharpClassificationService() :> IFSharpClassificationService

            let definitionItem =
                if isExternal then
                    externalDefinitionItem
                else
                    definitionItems
                    |> Array.tryFindV (fun struct (_, project: Project) -> project.FilePath = doc.Project.FilePath)
                    |> ValueOption.map (fun struct (definitionItem, _) -> definitionItem)
                    |> ValueOption.defaultValue externalDefinitionItem

            for symbolUse in symbolUses do
                cancellationToken.ThrowIfCancellationRequested()

                match declarationRange, RoslynHelpers.TryFSharpRangeToTextSpan(sourceText, symbolUse) with
                | ValueSome declRange, _ when Range.equals declRange symbolUse -> ()
                | _, ValueNone -> ()
                | _, ValueSome textSpan ->
                    match textSpan with
                    | Tokenizer.FixedSpan sourceText symbolName fixedSpan ->
                        // REVIEW: OnReferenceFoundAsync is throwing inside Roslyn, putting a try/with so find-all refs doesn't fail.
                        try
                            let! struct (classifiedSpans, highlightSpan) =
                                ClassifiedReferenceLine.classifyAsync classifier doc sourceText fixedSpan

                            do!
                                onReferenceFoundAsync (
                                    FSharpSourceReferenceItem(
                                        definitionItem,
                                        FSharpDocumentSpan(doc, fixedSpan),
                                        classifiedSpans,
                                        highlightSpan
                                    )
                                )
                        with error when not (error :? OperationCanceledException) ->
                            ()
                    | _ -> ()
        }

    // File can be included in more than one project, hence single `range` may results with multiple `Document`s.
    let rangeToDocumentSpans (document: Document, range: range, symbolName: string) =
        if range.Start = range.End then
            CancellableTask.singleton [||]
        else
            cancellableTask {
                let! spans =
                    seq {
                        for doc in document.GetSolutionDocumentsWithFilePath range.FileName do
                            cancellableTask {
                                let! cancellationToken = CancellableTask.getCancellationToken ()
                                let! sourceText = doc.GetTextAsync(cancellationToken)

                                match Tokenizer.TryFSharpRangeToTextSpanForEditor(sourceText, range, symbolName) with
                                | ValueSome fixedSpan -> return Some(FSharpDocumentSpan(doc, fixedSpan))
                                | ValueNone -> return None
                            }
                    }
                    |> CancellableTask.whenAll

                return spans |> Array.choose id
            }

    let private referencingCompilationProjects (declaringProject: Project) =
        match declaringProject.OutputFilePath with
        | null -> []
        | outputFilePath ->
            ProjectFiltering.getProjectsReferencingAssembly outputFilePath declaringProject.Solution
            |> List.filter (fun project -> not project.IsFSharp && project.SupportsCompilation)

    /// Locations in a C# or VB project of the symbol with the given documentation comment id.
    let private findRoslynReferences (docId: string) (project: Project) =
        cancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()
            // Building a compilation and walking it costs the same cores the F# search is using.
            do! SymbolHelpers.searchThrottle.WaitAsync cancellationToken

            try
                match! project.GetCompilationAsync cancellationToken with
                | null -> return Seq.empty
                | compilation ->
                    match DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation) with
                    | null -> return Seq.empty
                    | symbol ->
                        let! referencedSymbols =
                            SymbolFinder.FindReferencesAsync(
                                symbol,
                                project.Solution,
                                ImmutableHashSet.CreateRange project.Documents,
                                cancellationToken
                            )

                        return referencedSymbols |> Seq.collect _.Locations
            finally
                SymbolHelpers.searchThrottle.Release() |> ignore
        }

    // Every search may build a compilation, and those cost memory, not just a core.
    [<Literal>]
    let private ConcurrentCompilations = 4

    /// The uses in the C# and VB projects that reference the assembly of a project declaring the symbol,
    /// each with the definition item to report them under.
    let private findCrossLanguageReferences (docId: string) (definitionItems: struct (FSharpDefinitionItem * Project) seq) =
        seq {
            for struct (definitionItem, declaringProject) in definitionItems do
                for project in referencingCompilationProjects declaringProject -> struct (definitionItem, project)
        }
        |> Seq.distinctBy (fun struct (_, project) -> project.Id)
        |> Seq.map (fun struct (definitionItem, project) ->
            findRoslynReferences docId project
            |> CancellableTask.map (Seq.map (fun location -> struct (definitionItem, location))))
        |> CancellableTask.whenAllThrottled ConcurrentCompilations
        |> CancellableTask.map Seq.concat

    /// Reports each file span once: the target-framework instances of a consumer share their files.
    let private reportCrossLanguageReferences
        (found: struct (FSharpDefinitionItem * ReferenceLocation) seq)
        (onReferenceFoundAsync: FSharpSourceReferenceItem -> Task)
        =
        cancellableTask {
            let reported = HashSet<struct (string * TextSpan)>()

            for struct (definitionItem, location) in found do
                let span = location.Location.SourceSpan

                if reported.Add(struct (location.Document.FilePath, span)) then
                    do! onReferenceFoundAsync (FSharpSourceReferenceItem(definitionItem, FSharpDocumentSpan(location.Document, span)))
        }

    let findReferencedSymbolsAsync
        (document: Document, position: int, context: IFSharpFindUsagesContext, allReferences: bool, userOp: string)
        : CancellableTask<unit> =
        cancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()
            let! sourceText = document.GetTextAsync(cancellationToken)
            let textLine = sourceText.Lines.GetLineFromPosition(position).ToString()
            let lineNumber = sourceText.Lines.GetLinePosition(position).Line + 1

            match! document.TryFindFSharpLexerSymbolAsync(position, SymbolLookupKind.Greedy, false, false, userOp) with
            | None -> ()
            | Some symbol ->

                let! _, checkFileResults = document.GetFSharpParseAndCheckResultsAsync(userOp)

                let symbolUse =
                    checkFileResults.GetSymbolUseAtLocation(lineNumber, symbol.Ident.idRange.EndColumn, textLine, symbol.FullIsland)

                let declaration =
                    checkFileResults.GetDeclarationLocation(lineNumber, symbol.Ident.idRange.EndColumn, textLine, symbol.FullIsland, false)

                match symbolUse with
                | None -> ()
                | Some symbolUse ->

                    let tags =
                        FSharpGlyphTags.GetTags(Tokenizer.GetGlyphForSymbol(symbolUse.Symbol, symbol.Kind))

                    let declarationRange =
                        match declaration with
                        | FindDeclResult.DeclFound range -> ValueSome range
                        | _ -> ValueNone

                    let! declarationSpans =
                        match declarationRange with
                        | ValueSome range -> rangeToDocumentSpans (document, range, symbol.Ident.idText)
                        | ValueNone -> CancellableTask.singleton [||]

                    let declarationSpans =
                        declarationSpans
                        |> Array.distinctBy (fun x -> x.Document.FilePath, x.Document.Project.FilePath)

                    let isExternal = Array.isEmpty declarationSpans

                    let displayParts =
                        ImmutableArray.Create(Microsoft.CodeAnalysis.TaggedText(TextTags.Text, symbol.Ident.idText))

                    let originationParts =
                        ImmutableArray.Create(Microsoft.CodeAnalysis.TaggedText(TextTags.Assembly, symbolUse.Symbol.Assembly.SimpleName))

                    let externalDefinitionItem =
                        FSharpDefinitionItem.CreateNonNavigableItem(tags, displayParts, originationParts)

                    let definitionItems =
                        declarationSpans
                        |> Array.map (fun span -> struct (FSharpDefinitionItem.Create(tags, displayParts, span), span.Document.Project))

                    do!
                        definitionItems
                        |> Seq.map (fun struct (definitionItem, _) -> context.OnDefinitionFoundAsync(definitionItem))
                        |> Task.WhenAll

                    if isExternal then
                        do! context.OnDefinitionFoundAsync(externalDefinitionItem)

                    // Find Implementations wants the definitions alone: reporting a use is what
                    // `allReferences` gates, so searching for them would throw the whole search away.
                    if allReferences then
                        let onFound =
                            onSymbolFound
                                declarationRange
                                externalDefinitionItem
                                definitionItems
                                isExternal
                                symbol.Ident.idText
                                context.OnReferenceFoundAsync

                        // Searched alongside the F# projects, reported after them.
                        let crossLanguageSearch =
                            match symbolUse.Symbol.DocumentationCommentId with
                            | ValueSome docId when not isExternal && not symbolUse.Symbol.IsInternalToProject ->
                                findCrossLanguageReferences docId definitionItems
                            | _ -> Task.FromResult Seq.empty

                        do! SymbolHelpers.findSymbolUses symbolUse document checkFileResults onFound
                        let! found = crossLanguageSearch
                        do! reportCrossLanguageReferences found context.OnReferenceFoundAsync
        }

open FSharpFindUsagesService

[<Export(typeof<IFSharpFindUsagesService>)>]
type internal FSharpFindUsagesService [<ImportingConstructor>] () =
    interface IFSharpFindUsagesService with
        member _.FindReferencesAsync(document, position, context) =
            findReferencedSymbolsAsync (document, position, context, true, nameof (FSharpFindUsagesService))
            |> CancellableTask.startAsTask context.CancellationToken

        member _.FindImplementationsAsync(document, position, context) =
            findReferencedSymbolsAsync (document, position, context, false, nameof (FSharpFindUsagesService))
            |> CancellableTask.startAsTask context.CancellationToken
