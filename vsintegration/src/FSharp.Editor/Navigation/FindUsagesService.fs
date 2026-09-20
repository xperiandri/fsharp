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
                for project in ProjectFiltering.getCompilationProjectsReferencingOutputOf declaringProject ->
                    struct (definitionItem, project)
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

    /// The symbol under the caret, the check of its document and where the symbol is declared.
    let private tryFindSymbolAtCaretAsync (document: Document, position: int, userOp: string) =
        cancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()
            let! sourceText = document.GetTextAsync(cancellationToken)
            let textLine = sourceText.Lines.GetLineFromPosition(position).ToString()
            let lineNumber = sourceText.Lines.GetLinePosition(position).Line + 1

            match! document.TryFindFSharpLexerSymbolAsync(position, SymbolLookupKind.Greedy, false, false, userOp) with
            | None -> return ValueNone
            | Some symbol ->

                let! _, checkFileResults = document.GetFSharpParseAndCheckResultsAsync(userOp)

                match checkFileResults.GetSymbolUseAtLocation(lineNumber, symbol.Ident.idRange.EndColumn, textLine, symbol.FullIsland) with
                | None -> return ValueNone
                | Some symbolUse ->
                    let declaration =
                        checkFileResults.GetDeclarationLocation(
                            lineNumber,
                            symbol.Ident.idRange.EndColumn,
                            textLine,
                            symbol.FullIsland,
                            false
                        )

                    return
                        ValueSome
                            struct {|
                                Symbol = symbol
                                SymbolUse = symbolUse
                                CheckFileResults = checkFileResults
                                Declaration = declaration
                            |}
        }

    /// Reports where the symbol under the caret is declared: a definition item for each project declaring it, or one
    /// that cannot be navigated to for a symbol from an assembly.
    let private reportDeclarationsAsync
        (document: Document)
        (context: IFSharpFindUsagesContext)
        (symbol: LexerSymbol)
        (symbolUse: FSharp.Compiler.CodeAnalysis.FSharpSymbolUse)
        declaration
        =
        cancellableTask {
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

            return
                struct {|
                    DeclarationRange = declarationRange
                    DefinitionItems = definitionItems
                    ExternalDefinitionItem = externalDefinitionItem
                    IsExternal = isExternal
                |}
        }

    let findReferencedSymbolsAsync
        (document: Document, position: int, context: IFSharpFindUsagesContext, userOp: string)
        : CancellableTask<unit> =
        cancellableTask {
            match! tryFindSymbolAtCaretAsync (document, position, userOp) with
            | ValueNone -> ()
            | ValueSome caret ->
                let! cancellationToken = CancellableTask.getCancellationToken ()

                let! declarations = reportDeclarationsAsync document context caret.Symbol caret.SymbolUse caret.Declaration

                let onFound =
                    onSymbolFound
                        declarations.DeclarationRange
                        declarations.ExternalDefinitionItem
                        declarations.DefinitionItems
                        declarations.IsExternal
                        caret.Symbol.Ident.idText
                        context.OnReferenceFoundAsync

                // Searched alongside the F# projects, reported after them.
                let crossLanguageSearch =
                    match caret.SymbolUse.Symbol.DocumentationCommentId with
                    | ValueSome docId when not declarations.IsExternal && not caret.SymbolUse.Symbol.IsInternalToProject ->
                        findCrossLanguageReferences docId declarations.DefinitionItems cancellationToken
                    | _ -> Task.FromResult Seq.empty

                do! SymbolHelpers.findSymbolUses caret.SymbolUse document caret.CheckFileResults onFound
                let! found = crossLanguageSearch
                do! reportCrossLanguageReferences found context.OnReferenceFoundAsync
        }

    /// Reports what implements the symbol under the caret, or where it is declared when nothing does.
    let findImplementationsAsync
        (document: Document, position: int, context: IFSharpFindUsagesContext, userOp: string)
        : CancellableTask<unit> =
        cancellableTask {
            match! tryFindSymbolAtCaretAsync (document, position, userOp) with
            | ValueNone -> ()
            | ValueSome caret ->
                let! cancellationToken = CancellableTask.getCancellationToken ()
                do! context.SetSearchTitleAsync(String.Format(SR.ImplementationsOf(), caret.Symbol.Ident.idText))

                let report (implementation: FindImplementations.Implementation) =
                    cancellableTask {
                        let displayParts =
                            ImmutableArray.Create(Microsoft.CodeAnalysis.TaggedText(TextTags.Text, implementation.Name))

                        let span = FSharpDocumentSpan(implementation.Document, implementation.Span)
                        do! context.OnDefinitionFoundAsync(FSharpDefinitionItem.Create(implementation.Tags, displayParts, span))
                    }

                let declared =
                    FindImplementations.tryDeclaredAt caret.SymbolUse caret.CheckFileResults cancellationToken

                match! FindImplementations.findAsync (declared |> ValueOption.defaultValue caret.SymbolUse.Symbol) document report with
                | 0 ->
                    // At an override, the declaration found for the caret is the abstract member's.
                    let declaration =
                        match declared with
                        | ValueSome declared ->
                            match declared.ImplementationLocation with
                            | Some range -> FindDeclResult.DeclFound range
                            | None -> caret.Declaration
                        | ValueNone -> caret.Declaration

                    do!
                        reportDeclarationsAsync document context caret.Symbol caret.SymbolUse declaration
                        |> CancellableTask.ignore
                | _ -> ()
        }

open FSharpFindUsagesService

[<Export(typeof<IFSharpFindUsagesService>)>]
type internal FSharpFindUsagesService [<ImportingConstructor>] () =
    interface IFSharpFindUsagesService with
        member _.FindReferencesAsync(document, position, context) =
            findReferencedSymbolsAsync (document, position, context, nameof (FSharpFindUsagesService))
            |> CancellableTask.startAsTask context.CancellationToken

        member _.FindImplementationsAsync(document, position, context) =
            findImplementationsAsync (document, position, context, nameof (FSharpFindUsagesService))
            |> CancellableTask.startAsTask context.CancellationToken
