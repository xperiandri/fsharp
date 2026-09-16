// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Linq

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.Navigation

open Microsoft.VisualStudio
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.LanguageServices

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.Symbols
open System.Composition
open System.Text.RegularExpressions
open CancellableTasks
open Microsoft.VisualStudio.FSharp.Editor.Telemetry
open Microsoft.VisualStudio.Telemetry
open Microsoft.VisualStudio.Threading

module private Symbol =
    let fullName (root: ISymbol) : string =
        let rec inner parts (sym: ISymbol) =
            match sym with
            | null -> parts
            // TODO: do we have any other terminating cases?
            | sym when sym.Kind = SymbolKind.NetModule || sym.Kind = SymbolKind.Assembly -> parts
            | sym when sym.MetadataName <> "" -> inner (sym.MetadataName :: parts) sym.ContainingSymbol
            | sym -> inner parts sym.ContainingSymbol

        inner [] root |> String.concat "."

module private FindDeclExternalType =
    let rec tryOfRoslynType (typesym: ITypeSymbol) : FindDeclExternalType voption =
        match typesym with
        | :? IPointerTypeSymbol as ptrparam ->
            tryOfRoslynType ptrparam.PointedAtType
            |> ValueOption.map FindDeclExternalType.Pointer
        | :? IArrayTypeSymbol as arrparam ->
            tryOfRoslynType arrparam.ElementType
            |> ValueOption.map FindDeclExternalType.Array
        | :? ITypeParameterSymbol as typaram -> ValueSome(FindDeclExternalType.TypeVar typaram.Name)
        | :? INamedTypeSymbol as namedTypeSym ->
            namedTypeSym.TypeArguments
            |> Seq.map tryOfRoslynType
            |> List.ofSeq
            |> ValueOption.ofValueOptionList
            |> ValueOption.map (fun genericArgs -> FindDeclExternalType.Type(Symbol.fullName typesym, genericArgs))
        | _ ->
            Debug.Assert(false, sprintf "GoToDefinitionService: Unexpected Roslyn type symbol subclass: %O" (typesym.GetType()))
            ValueNone

module private FindDeclExternalParam =

    let tryOfRoslynParameter (param: IParameterSymbol) : FindDeclExternalParam voption =
        FindDeclExternalType.tryOfRoslynType param.Type
        |> ValueOption.map (fun ty -> FindDeclExternalParam.Create(ty, param.RefKind <> RefKind.None))

    let tryOfRoslynParameters (paramSyms: ImmutableArray<IParameterSymbol>) : FindDeclExternalParam list voption =
        paramSyms
        |> Seq.map tryOfRoslynParameter
        |> Seq.toList
        |> ValueOption.ofValueOptionList

module private ExternalSymbol =
    let rec ofRoslynSymbol (symbol: ISymbol) : struct (ISymbol * FindDeclExternalSymbol) list =
        let container = Symbol.fullName symbol.ContainingSymbol

        match symbol with
        | :? INamedTypeSymbol as typesym ->
            let fullTypeName = Symbol.fullName typesym

            let constructors =
                typesym.InstanceConstructors
                |> Seq.chooseV<_, struct (ISymbol * FindDeclExternalSymbol)> (fun methsym ->
                    FindDeclExternalParam.tryOfRoslynParameters methsym.Parameters
                    |> ValueOption.map (fun args -> struct (methsym, FindDeclExternalSymbol.Constructor(fullTypeName, args))))
                |> List.ofSeq

            struct (symbol, FindDeclExternalSymbol.Type fullTypeName) :: constructors

        | :? IMethodSymbol as methsym ->
            FindDeclExternalParam.tryOfRoslynParameters methsym.Parameters
            |> ValueOption.map (fun args ->
                struct (symbol, FindDeclExternalSymbol.Method(container, methsym.MetadataName, args, methsym.TypeParameters.Length)))
            |> ValueOption.toList

        | :? IPropertySymbol as propsym ->
            [
                upcast propsym, FindDeclExternalSymbol.Property(container, propsym.MetadataName)
            ]

        | :? IFieldSymbol as fieldsym ->
            [
                upcast fieldsym, FindDeclExternalSymbol.Field(container, fieldsym.MetadataName)
            ]

        | :? IEventSymbol as eventsym ->
            [
                upcast eventsym, FindDeclExternalSymbol.Event(container, eventsym.MetadataName)
            ]

        | _ -> []

    /// The C# or Visual Basic symbol an F# resolution points at. Its documentation comment id names
    /// it outright; walking every declaration of the project is what answers for the symbols whose id
    /// Roslyn cannot parse, operators among them.
    let tryFind (project: Project) (targetSymbolUse: FSharpSymbolUse) targetExternalSymbol =
        cancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()
            let! compilation = project.GetCompilationAsync cancellationToken

            let named =
                match compilation, targetSymbolUse.Symbol.DocumentationCommentId with
                | null, _
                | _, ValueNone -> ValueNone
                | compilation, ValueSome documentationCommentId ->
                    match DocumentationCommentId.GetFirstSymbolForDeclarationId(documentationCommentId, compilation) with
                    | null -> ValueNone
                    | symbol -> ValueSome symbol

            match named with
            | ValueSome symbol -> return ValueSome symbol
            | ValueNone ->
                let! symbols = SymbolFinder.FindSourceDeclarationsAsync(project, (fun _ -> true), cancellationToken)

                return
                    symbols
                    |> Seq.collect ofRoslynSymbol
                    |> Seq.tryPickV (fun struct (symbol, externalSymbol) ->
                        if externalSymbol = targetExternalSymbol then
                            ValueSome symbol
                        else
                            ValueNone)
        }

type internal FSharpGoToDefinitionNavigableItem(document, sourceSpan) =
    inherit FSharpNavigableItem(Glyph.BasicFile, ImmutableArray.Empty, document, sourceSpan)

[<RequireQualifiedAccess>]
type internal FSharpGoToDefinitionResult =
    | NavigableItem of FSharpNavigableItem
    | ExternalAssembly of FSharpSymbolUse * MetadataReference seq

type internal GoToDefinition(metadataAsSource: FSharpMetadataAsSourceService) =

    let rec areTypesEqual (ty1: FSharpType) (ty2: FSharpType) =
        let ty1 = ty1.StripAbbreviations()
        let ty2 = ty2.StripAbbreviations()

        let generic =
            ty1.IsGenericParameter && ty2.IsGenericParameter
            || (ty1.GenericArguments.Count = ty2.GenericArguments.Count
                && (ty1.GenericArguments, ty2.GenericArguments) ||> Seq.forall2 areTypesEqual)

        if generic then
            true
        else
            let namesEqual = ty1.TypeDefinition.DisplayName = ty2.TypeDefinition.DisplayName
            let accessPathsEqual = ty1.TypeDefinition.AccessPath = ty2.TypeDefinition.AccessPath
            namesEqual && accessPathsEqual

    let tryFindExternalSymbolUse (targetSymbolUse: FSharpSymbolUse) (x: FSharpSymbolUse) =
        match x.Symbol, targetSymbolUse.Symbol with
        | (:? FSharpEntity as symbol1), (:? FSharpEntity as symbol2) when x.IsFromDefinition -> symbol1.DisplayName = symbol2.DisplayName

        | (:? FSharpMemberOrFunctionOrValue as symbol1), (:? FSharpMemberOrFunctionOrValue as symbol2) ->
            symbol1.DisplayName = symbol2.DisplayName
            && (match symbol1.DeclaringEntity, symbol2.DeclaringEntity with
                | Some e1, Some e2 -> e1.CompiledName = e2.CompiledName
                | _ -> false)
            && symbol1.GenericParameters.Count = symbol2.GenericParameters.Count
            && symbol1.CurriedParameterGroups.Count = symbol2.CurriedParameterGroups.Count
            && ((symbol1.CurriedParameterGroups, symbol2.CurriedParameterGroups)
                ||> Seq.forall2 (fun pg1 pg2 ->
                    let pg1, pg2 = pg1.ToArray(), pg2.ToArray()
                    // We filter out/fixup first "unit" parameter in the group, since it just represents the `()` call notation, for example `"string".Clone()` will have one curried group with one parameter which type is unit.
                    let pg1 = // If parameter has no name and it's unit type, filter it out
                        if
                            pg1.Length > 0
                            && Option.isNone pg1[0].Name
                            && pg1[0].Type.StripAbbreviations().TypeDefinition.DisplayName = "Unit"
                        then
                            pg1[1..]
                        else
                            pg1

                    pg1.Length = pg2.Length
                    && ((pg1, pg2) ||> Seq.forall2 (fun p1 p2 -> areTypesEqual p1.Type p2.Type))))
            && areTypesEqual symbol1.ReturnParameter.Type symbol2.ReturnParameter.Type
        | (:? FSharpField as symbol1), (:? FSharpField as symbol2) when x.IsFromDefinition ->
            symbol1.DisplayName = symbol2.DisplayName
            && (match symbol1.DeclaringEntity, symbol2.DeclaringEntity with
                | Some e1, Some e2 -> e1.CompiledName = e2.CompiledName
                | _ -> false)
        | (:? FSharpUnionCase as symbol1), (:? FSharpUnionCase as symbol2) ->
            symbol1.DisplayName = symbol2.DisplayName
            && symbol1.DeclaringEntity.CompiledName = symbol2.DeclaringEntity.CompiledName
        | _ -> false

    /// The navigable item for the range in the document, when the range fits the document's text.
    let navigableItemAt (document: Document) (range: range) =
        cancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()
            let! sourceText = document.GetTextAsync(cancellationToken)

            return
                RoslynHelpers.TryFSharpRangeToTextSpan(sourceText, range)
                |> ValueOption.map (fun textSpan -> FSharpGoToDefinitionNavigableItem(document, textSpan))
        }

    /// Use an origin document to provide the solution & workspace used to
    /// find the corresponding textSpan and INavigableItem for the range
    let rangeToNavigableItem (range: range, document: Document) =
        cancellableTask {
            match document.TryGetSolutionDocumentFromFSharpRange range with
            | ValueNone -> return None
            | ValueSome refDocument ->
                let! navItem = navigableItemAt refDocument range
                return ValueOption.toOption navItem
        }

    member _.TryGetExternalDeclarationAsync(targetSymbolUse: FSharpSymbolUse, metadataReferences: seq<MetadataReference>) =
        let textOpt =
            match targetSymbolUse.Symbol with
            | :? FSharpEntity as symbol -> symbol.TryGetMetadataText() |> Option.map (fun text -> text, symbol.DisplayName)
            | :? FSharpMemberOrFunctionOrValue as symbol ->
                symbol.ApparentEnclosingEntity
                |> Option.bind (fun entity -> entity.TryGetMetadataText() |> Option.map (fun text -> text, entity.DisplayName))

            | :? FSharpField as symbol ->
                match symbol.DeclaringEntity with
                | Some entity ->
                    let text = entity.TryGetMetadataText()

                    match text with
                    | Some text -> Some(text, entity.DisplayName)
                    | None -> None
                | None -> None
            | :? FSharpUnionCase as symbol ->
                symbol.DeclaringEntity.TryGetMetadataText()
                |> Option.map (fun text -> text, symbol.DisplayName)
            | _ -> None

        match textOpt with
        | None -> CancellableTask.singleton None
        | Some(text, fileName) ->
            cancellableTask {
                let! cancellationToken = CancellableTask.getCancellationToken ()

                let tmpProjInfo, tmpDocInfo =
                    MetadataAsSource.generateTemporaryDocument (
                        AssemblyIdentity(targetSymbolUse.Symbol.Assembly.QualifiedName),
                        fileName,
                        metadataReferences
                    )

                metadataAsSource.WriteDocument(tmpDocInfo.FilePath, SourceText.From(text.ToString()))

                // Opening the document is the only part that belongs to the main thread; checking the
                // signature it generates is a type check like any other.
                do! ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken)
                let tmpShownDocOpt = metadataAsSource.ShowDocument(tmpProjInfo, tmpDocInfo.FilePath)
                do! TaskScheduler.Default.SwitchTo()

                match tmpShownDocOpt with
                | ValueNone -> return None
                | ValueSome tmpShownDoc ->
                    let! _, checkResults = tmpShownDoc.GetFSharpParseAndCheckResultsAsync("NavigateToExternalDeclaration")

                    let r =
                        // This tries to find the best possible location of the target symbol's location in the metadata source.
                        // We really should rely on symbol equality within FCS instead of doing it here,
                        //     but the generated metadata as source isn't perfect for symbol equality.
                        let symbols = checkResults.GetAllUsesOfAllSymbolsInFile(cancellationToken)

                        symbols
                        |> Seq.tryFindV (tryFindExternalSymbolUse targetSymbolUse)
                        |> ValueOption.map (fun x -> x.Range)

                    let! span =
                        cancellableTask {
                            let! cancellationToken = CancellableTask.getCancellationToken ()

                            match r with
                            | ValueNone -> return TextSpan.empty
                            | ValueSome r ->
                                let! text = tmpShownDoc.GetTextAsync(cancellationToken)

                                match RoslynHelpers.TryFSharpRangeToTextSpan(text, r) with
                                | ValueSome span -> return span
                                | _ -> return TextSpan.empty
                        }

                    return Some(FSharpGoToDefinitionNavigableItem(tmpShownDoc, span) :> FSharpNavigableItem)
            }

    /// Helper function that is used to determine the navigation strategy to apply, can be tuned towards signatures or implementation files.
    member private _.FindSymbolHelper(originDocument: Document, originRange: range, sourceText: SourceText, preferSignature: bool) =
        let originTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan(sourceText, originRange)

        match originTextSpan with
        | ValueNone -> CancellableTask.singleton None
        | ValueSome originTextSpan ->
            cancellableTask {
                let userOpName = "FindSymbolHelper"

                let position = originTextSpan.Start
                let! lexerSymbol = originDocument.TryFindFSharpLexerSymbolAsync(position, SymbolLookupKind.Greedy, false, false, userOpName)

                match lexerSymbol with
                | None -> return None
                | Some lexerSymbol ->
                    let textLinePos = sourceText.Lines.GetLinePosition position
                    let fcsTextLineNumber = Line.fromZ textLinePos.Line
                    let lineText = (sourceText.Lines.GetLineFromPosition position).ToString()
                    let idRange = lexerSymbol.Ident.idRange

                    let! ct = CancellableTask.getCancellationToken ()

                    let! _, checkFileResults = originDocument.GetFSharpParseAndCheckResultsAsync(nameof (GoToDefinition))

                    let fsSymbolUse =
                        checkFileResults.GetSymbolUseAtLocation(fcsTextLineNumber, idRange.EndColumn, lineText, lexerSymbol.FullIsland)

                    match fsSymbolUse with
                    | None -> return None
                    | Some fsSymbolUse ->

                        let symbol = fsSymbolUse.Symbol
                        // if the tooltip was spawned in an implementation file and we have a range targeting
                        // a signature file, try to find the corresponding implementation file and target the
                        // desired symbol
                        if isSignatureFile fsSymbolUse.FileName && preferSignature = false then
                            let fsfilePath = Path.ChangeExtension(originRange.FileName, "fs")

                            // A file the solution does not hold cannot be navigated to, so asking it
                            // answers what asking the disk would, without leaving the workspace.
                            match originDocument.TryGetSolutionDocumentFromPath fsfilePath with
                            | ValueNone -> return None
                            | ValueSome implDoc ->
                                let! implSourceText = implDoc.GetTextAsync(ct)

                                let! _, checkFileResults = implDoc.GetFSharpParseAndCheckResultsAsync(userOpName)

                                let symbolUses =
                                    checkFileResults.GetUsesOfSymbolInFile(symbol, cancellationToken = ct)

                                let implSymbol = Array.tryHeadV symbolUses

                                match implSymbol with
                                | ValueNone -> return None
                                | ValueSome implSymbol ->
                                    let implTextSpan =
                                        RoslynHelpers.TryFSharpRangeToTextSpan(implSourceText, implSymbol.Range)

                                    match implTextSpan with
                                    | ValueNone -> return None
                                    | ValueSome implTextSpan -> return Some(FSharpGoToDefinitionNavigableItem(implDoc, implTextSpan))
                        else
                            match originDocument.TryGetSolutionDocumentFromFSharpRange fsSymbolUse.Range with
                            | ValueNone -> return None
                            | ValueSome targetDocument -> return! rangeToNavigableItem (fsSymbolUse.Range, targetDocument)
            }

    /// if the symbol is defined in the given file, return its declaration location, otherwise use the targetSymbol to find the first
    /// instance of its presence in the provided source file. The first case is needed to return proper declaration location for
    /// recursive type definitions, where the first its usage may not be the declaration.
    member _.FindSymbolDeclarationInDocument(targetSymbolUse: FSharpSymbolUse, document: Document) =
        cancellableTask {
            let filePath = document.FilePath

            let inThisFile =
                function
                | Some(range: range) when range.FileName |> isTheFileAt filePath -> ValueSome range
                | _ -> ValueNone

            let symbol = targetSymbolUse.Symbol

            // A symbol imported from a project with signature files carries its implementation range
            // beside its declaration one; either of them being this file spares a check of the file.
            let knownRange =
                inThisFile symbol.ImplementationLocation
                |> ValueOption.orElseWith (fun () -> inThisFile symbol.DeclarationLocation)
                |> ValueOption.orElseWith (fun () -> inThisFile symbol.SignatureLocation)

            match knownRange with
            | ValueSome range -> return Some range
            | ValueNone ->
                let! _, checkFileResults = document.GetFSharpParseAndCheckResultsAsync("FindSymbolDeclarationInDocument")

                let symbolUses = checkFileResults.GetUsesOfSymbolInFile targetSymbolUse.Symbol

                return
                    symbolUses
                    |> Seq.sortByDescending _.IsFromDefinition
                    |> Seq.tryHeadV
                    |> ValueOption.map _.Range
                    |> ValueOption.toOption
        }

    /// The navigable item for the target symbol's declaration in the implementation document.
    member private this.FindNavigableDeclarationIn(targetSymbolUse: FSharpSymbolUse, implDocument: Document) =
        cancellableTask {
            match! this.FindSymbolDeclarationInDocument(targetSymbolUse, implDocument) with
            | None -> return ValueNone
            | Some declarationRange -> return! navigableItemAt implDocument declarationRange
        }

    /// The caret is already on the declaration: in a signature file the target is the implementation,
    /// in an implementation file it is the signature.
    member private this.FindCounterpartOfDeclarationAtCaret
        (
            originDocument: Document,
            targetSymbolUse: FSharpSymbolUse,
            checkFileResults: FSharpCheckFileResults,
            lexerSymbol: LexerSymbol,
            fcsTextLineNumber: int,
            textLineString: string
        ) =
        cancellableTask {
            if isSignatureFile originDocument.FilePath then
                let implFilePath = Path.ChangeExtension(originDocument.FilePath, "fs")

                match originDocument.TryGetSolutionDocumentFromPath implFilePath with
                | ValueNone -> return ValueNone
                | ValueSome implDocument -> return! this.FindNavigableDeclarationIn(targetSymbolUse, implDocument)
            else
                let declarations =
                    checkFileResults.GetDeclarationLocation(
                        fcsTextLineNumber,
                        lexerSymbol.Ident.idRange.EndColumn,
                        textLineString,
                        lexerSymbol.FullIsland,
                        true
                    )

                match declarations with
                | FindDeclResult.DeclFound sigRange ->
                    match originDocument.TryGetSolutionDocumentFromFSharpRange sigRange with
                    | ValueNone -> return ValueNone
                    | ValueSome sigDocument -> return! navigableItemAt sigDocument sigRange
                | _ -> return ValueNone
        }

    member private this.FindAtPosition(originDocument: Document, position: int, preferSignature: bool, counterpartAtCaret: bool) =
        cancellableTask {
            let userOpName = "FindDefinitionAtPosition"
            let! cancellationToken = CancellableTask.getCancellationToken ()
            let! sourceText = originDocument.GetTextAsync(cancellationToken)
            let textLine = sourceText.Lines.GetLineFromPosition position
            let textLinePos = sourceText.Lines.GetLinePosition position
            let textLineString = textLine.ToString()
            let fcsTextLineNumber = Line.fromZ textLinePos.Line
            let lineText = (sourceText.Lines.GetLineFromPosition position).ToString()

            let! lexerSymbol = originDocument.TryFindFSharpLexerSymbolAsync(position, SymbolLookupKind.Greedy, false, false, userOpName)

            match lexerSymbol with
            | None -> return ValueNone
            | Some lexerSymbol ->

                let idRange = lexerSymbol.Ident.idRange

                let! _, checkFileResults = originDocument.GetFSharpParseAndCheckResultsAsync(userOpName)

                let declarations =
                    checkFileResults.GetDeclarationLocation(
                        fcsTextLineNumber,
                        idRange.EndColumn,
                        textLineString,
                        lexerSymbol.FullIsland,
                        preferSignature
                    )

                let targetSymbolUse =
                    checkFileResults.GetSymbolUseAtLocation(fcsTextLineNumber, idRange.EndColumn, lineText, lexerSymbol.FullIsland)

                match targetSymbolUse with
                | None -> return ValueNone
                | Some targetSymbolUse ->

                    match declarations with
                    | FindDeclResult.ExternalDecl(assembly, targetExternalSym) ->
                        let projectOpt =
                            originDocument.TryFindInSolutions(fun solution ->
                                solution.Projects
                                |> Seq.tryFindV (fun p -> p.AssemblyName.Equals(assembly, StringComparison.OrdinalIgnoreCase)))

                        match projectOpt with
                        | ValueSome project ->
                            let! symbol = ExternalSymbol.tryFind project targetSymbolUse targetExternalSym

                            let location =
                                symbol |> ValueOption.map _.Locations |> ValueOption.bind Seq.tryHeadV

                            match location with
                            | ValueNone -> return ValueNone
                            | ValueSome location ->

                                return
                                    ValueSome(
                                        FSharpGoToDefinitionResult.NavigableItem(
                                            FSharpGoToDefinitionNavigableItem(project.GetDocument(location.SourceTree), location.SourceSpan)
                                        ),
                                        idRange
                                    )
                        | _ ->
                            let metadataReferences = originDocument.Project.MetadataReferences
                            return ValueSome(FSharpGoToDefinitionResult.ExternalAssembly(targetSymbolUse, metadataReferences), idRange)

                    | FindDeclResult.DeclFound targetRange ->
                        match originDocument.TryGetSolutionDocumentFromFSharpRange targetRange with
                        | ValueNone ->
                            // No document for the file anywhere in the workspace: the symbol comes from an assembly.
                            let metadataReferences = originDocument.Project.MetadataReferences
                            return ValueSome(FSharpGoToDefinitionResult.ExternalAssembly(targetSymbolUse, metadataReferences), idRange)
                        | ValueSome targetDocument when lexerSymbol.Range = targetRange ->
                            let! navItem =
                                if counterpartAtCaret then
                                    this.FindCounterpartOfDeclarationAtCaret(
                                        originDocument,
                                        targetSymbolUse,
                                        checkFileResults,
                                        lexerSymbol,
                                        fcsTextLineNumber,
                                        textLineString
                                    )
                                else
                                    navigableItemAt targetDocument targetRange

                            return
                                navItem
                                |> ValueOption.map (fun navItem -> FSharpGoToDefinitionResult.NavigableItem navItem, idRange)
                        | ValueSome targetDocument ->
                            // gotoDefn origin = signature, destination = signature; origin = implementation, destination = implementation
                            let! navItem =
                                if isSignatureFile targetRange.FileName && preferSignature then
                                    navigableItemAt targetDocument targetRange
                                else
                                    // Bugfix: apparently the target document is not always a signature file
                                    let implFilePath =
                                        if isSignatureFile targetDocument.FilePath then
                                            Path.ChangeExtension(targetDocument.FilePath, "fs")
                                        else
                                            targetDocument.FilePath

                                    match originDocument.TryGetSolutionDocumentFromPath implFilePath with
                                    | ValueNone -> CancellableTask.singleton ValueNone
                                    | ValueSome implDocument -> this.FindNavigableDeclarationIn(targetSymbolUse, implDocument)

                            return
                                navItem
                                |> ValueOption.map (fun navItem -> FSharpGoToDefinitionResult.NavigableItem navItem, idRange)
                    | _ -> return ValueNone
        }

    /// Go To Definition: from an implementation file the definition, from a signature file the declaration; at either
    /// of them, the other one.
    member internal this.FindDefinitionAtPosition(originDocument: Document, position: int) =
        this.FindAtPosition(originDocument, position, isSignatureFile originDocument.FilePath, true)

    /// Go To Declaration: the declaration in the signature file when there is one, otherwise the definition; at a
    /// declaration, the declaration itself.
    member internal this.FindDeclarationAtPosition(originDocument: Document, position: int) =
        this.FindAtPosition(originDocument, position, true, false)

    /// find the declaration location (signature file/.fsi) of the target symbol if possible, fall back to definition
    member this.FindDeclarationOfSymbolAtRange(targetDocument: Document, symbolRange: range, targetSource: SourceText) =
        this.FindSymbolHelper(targetDocument, symbolRange, targetSource, preferSignature = true)

    /// find the definition location (implementation file/.fs) of the target symbol
    member this.FindDefinitionOfSymbolAtRange(targetDocument: Document, symbolRange: range, targetSourceText: SourceText) =
        this.FindSymbolHelper(targetDocument, symbolRange, targetSourceText, preferSignature = false)

    /// Construct a task that will return a navigation target for the implementation definition of the symbol
    /// at the provided position in the document.
    member this.FindDefinitionAsync(originDocument: Document, position: int) =
        this.FindDefinitionAtPosition(originDocument, position)

    /// Navigate to the position of the textSpan in the provided document
    /// used by quickinfo link navigation when the tooltip contains the correct destination range.
    member _.TryNavigateToTextSpan(document: Document, textSpan: TextSpan, cancellationToken: CancellationToken) =
        let navigableItem = FSharpGoToDefinitionNavigableItem(document, textSpan)
        let workspace = document.Project.Solution.Workspace

        let navigationService =
            workspace.Services.GetService<IFSharpDocumentNavigationService>()

        navigationService.TryNavigateToSpan(workspace, navigableItem.Document.Id, navigableItem.SourceSpan, cancellationToken)
        |> ignore

    member _.NavigateToItem(navigableItem: FSharpNavigableItem, cancellationToken: CancellationToken) =

        let workspace = navigableItem.Document.Project.Solution.Workspace

        let navigationService =
            workspace.Services.GetService<IFSharpDocumentNavigationService>()

        // Prefer open documents in the preview tab.
        let result =
            navigationService.TryNavigateToSpan(workspace, navigableItem.Document.Id, navigableItem.SourceSpan, cancellationToken)

        result

    /// Find the declaration location (signature file/.fsi) of the target symbol if possible, fall back to definition
    member this.NavigateToSymbolDeclarationAsync(targetDocument: Document, targetSourceText: SourceText, symbolRange: range) =
        cancellableTask {
            let! item = this.FindDeclarationOfSymbolAtRange(targetDocument, symbolRange, targetSourceText)

            match item with
            | None -> return false
            | Some item ->
                let! cancellationToken = CancellableTask.getCancellationToken ()
                return this.NavigateToItem(item, cancellationToken)
        }

    /// Find the definition location (implementation file/.fs) of the target symbol
    member this.NavigateToSymbolDefinitionAsync(targetDocument: Document, targetSourceText: SourceText, symbolRange: range) =
        cancellableTask {
            let! item = this.FindDefinitionOfSymbolAtRange(targetDocument, symbolRange, targetSourceText)

            match item with
            | None -> return false
            | Some item ->
                let! cancellationToken = CancellableTask.getCancellationToken ()
                return this.NavigateToItem(item, cancellationToken)
        }

    member this.NavigateToExternalDeclarationAsync(targetSymbolUse: FSharpSymbolUse, metadataReferences: seq<MetadataReference>) =
        foregroundCancellableTask {
            let! cancellationToken = CancellableTask.getCancellationToken ()

            match! this.TryGetExternalDeclarationAsync(targetSymbolUse, metadataReferences) with
            | Some navItem -> return this.NavigateToItem(navItem, cancellationToken)
            | None -> return false
        }

type internal FSharpNavigation(metadataAsSource: FSharpMetadataAsSourceService, initialDoc: Document, thisSymbolUseRange: range) =

    let workspace = initialDoc.Project.Solution.Workspace
    let solution = workspace.CurrentSolution

    member _.IsTargetValid(range: range) =
        range <> rangeStartup
        && range <> thisSymbolUseRange
        && solution.TryGetDocumentIdFromFSharpRange(range, initialDoc.Project.Id)
           |> Option.isSome

    /// Follows a link in a QuickInfo tooltip, which the user clicks on the main thread. Nothing waits
    /// for the result, so the search runs in the background and only the navigation comes back here.
    member _.NavigateTo(range: range) =
        let navigation =
            ThreadHelper.JoinableTaskFactory.RunAsync(fun () ->
                cancellableTask {
                    let targetDoc = solution.TryGetDocumentFromFSharpRange(range, initialDoc.Project.Id)

                    match targetDoc with
                    | None -> ()
                    | Some targetDoc ->

                        let! cancellationToken = CancellableTask.getCancellationToken ()

                        let! targetSource = targetDoc.GetTextAsync(cancellationToken)
                        let targetTextSpan = RoslynHelpers.TryFSharpRangeToTextSpan(targetSource, range)

                        match targetTextSpan with
                        | ValueNone -> ()
                        | ValueSome targetTextSpan ->

                            let gtd = GoToDefinition(metadataAsSource)

                            // Whenever possible:
                            //  - signature files (.fsi) should navigate to other signature files
                            //  - implementation files (.fs) should navigate to other implementation files
                            if isSignatureFile initialDoc.FilePath then
                                // Target range will point to .fsi file if only there is one so we can just use Roslyn navigation service.
                                do gtd.TryNavigateToTextSpan(targetDoc, targetTextSpan, cancellationToken)
                            else
                                // Navigation request was made in a .fs file, so we try to find the implementation of the symbol at target range.
                                // This is the part that may take some time, because of type checks involved.
                                let! result = gtd.NavigateToSymbolDefinitionAsync(targetDoc, targetSource, range)

                                if not result then
                                    // In case the above fails, we just navigate to target range.
                                    do gtd.TryNavigateToTextSpan(targetDoc, targetTextSpan, cancellationToken)
                }
                |> CancellableTask.startAsTaskWithoutCancellation)

        navigation.FileAndForget "fsharp/navigateToQuickInfoTarget"

    member _.FindDefinitionsAsync(position) =
        cancellableTask {
            let gtd = GoToDefinition(metadataAsSource)
            let! result = gtd.FindDefinitionAtPosition(initialDoc, position)

            match result with
            | ValueSome(FSharpGoToDefinitionResult.NavigableItem(navItem), _) -> return ImmutableArray.create navItem
            | ValueSome(FSharpGoToDefinitionResult.ExternalAssembly(targetSymbolUse, metadataReferences), _) ->
                match! gtd.TryGetExternalDeclarationAsync(targetSymbolUse, metadataReferences) with
                | Some navItem -> return ImmutableArray.Create navItem
                | _ -> return ImmutableArray.empty
            | _ -> return ImmutableArray.empty
        }

    /// The same search, minus the definitions that only exist once a metadata document has been generated:
    /// generating one takes the main thread, and Peek's broker holds it in `JoinableTaskFactory.Run` without
    /// pumping messages until this returns, so asking for it there deadlocks Visual Studio.
    member _.FindDefinitionsWithoutMetadataAsync(position) =
        cancellableTask {
            let gtd = GoToDefinition(metadataAsSource)
            let! result = gtd.FindDefinitionAtPosition(initialDoc, position)

            match result with
            | ValueSome(FSharpGoToDefinitionResult.NavigableItem(navItem), _) -> return ImmutableArray.create navItem
            | _ -> return ImmutableArray.empty
        }

    member _.TryGoToDefinition(position, cancellationToken) =
        // Once we migrate to Roslyn-exposed MAAS and sourcelink (https://github.com/dotnet/fsharp/issues/13951), this can be a "normal" task.
        // The IFSharpGoToDefinitionService contract is synchronous, so the main thread has to wait here: the threaded-wait dialog
        // keeps it pumping and cancellable, where a bare Task.Wait froze it until the VS watchdog auto-cancelled.
        try
            use _ =
                TelemetryReporter.ReportSingleEventWithDuration(TelemetryEvents.GoToDefinition, [||])

            let gtd = GoToDefinition(metadataAsSource)
            let navigated = ref false

            ThreadHelper.JoinableTaskFactory.Run(
                SR.NavigatingTo(),
                (fun _progress dialogCancellationToken ->
                    let linked =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dialogCancellationToken)

                    cancellableTask {
                        use _ = linked

                        match! gtd.FindDefinitionAsync(initialDoc, position) with
                        | ValueSome(FSharpGoToDefinitionResult.NavigableItem(navItem), _) ->
                            gtd.NavigateToItem(navItem, linked.Token) |> ignore
                            navigated.Value <- true
                        | ValueSome(FSharpGoToDefinitionResult.ExternalAssembly(targetSymbolUse, metadataReferences), _) ->
                            let! result = gtd.NavigateToExternalDeclarationAsync(targetSymbolUse, metadataReferences)
                            navigated.Value <- result
                        | _ -> ()
                    }
                    |> CancellableTask.start linked.Token),
                TimeSpan.FromSeconds 1
            )

            navigated.Value
        with
        | :? OperationCanceledException -> false
        | exc ->
            TelemetryReporter.ReportFault(TelemetryEvents.GoToDefinition, FaultSeverity.General, exc)
            false
