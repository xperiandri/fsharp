// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Go To Implementation: the types implementing an interface or deriving from a class, and the members implementing an
/// abstract member, found the way Roslyn finds them for C#. The inheritance sites of each file pick the files to check,
/// and the check results confirm the candidates.
module internal Microsoft.VisualStudio.FSharp.Editor.FindImplementations

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Collections.Immutable

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.ExternalAccess.FSharp
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open CancellableTasks
open InheritanceSites

/// A declaration implementing what Go To Implementation was asked about.
[<Struct>]
type Implementation =
    {
        Document: Document
        Span: TextSpan
        Name: string
        Tags: ImmutableArray<string>
    }

[<RequireQualifiedAccess>]
type private Target =
    /// The types implementing an interface or deriving from a class.
    | DerivedTypes of FSharpEntity
    /// The members implementing an abstract member, in its type and the types deriving from it.
    | Slot of FSharpMemberOrFunctionOrValue * declaringEntity: FSharpEntity
    /// The members implementing what an override implements, in the types deriving from the override's type.
    | Override of FSharpMemberOrFunctionOrValue * declaringEntity: FSharpEntity
    | Self

[<Literal>]
let private UserOpName = "FindImplementations"

let private definitionOf (entity: FSharpEntity) =
    if entity.IsFSharpAbbreviation then
        match entity.AbbreviatedType.StripAbbreviations() with
        | abbreviated when abbreviated.HasTypeDefinition -> abbreviated.TypeDefinition
        | _ -> entity
    else
        entity

/// The type a type name stands for: a constructor names its type, an abbreviation what it abbreviates.
let private entityOf (symbol: FSharpSymbol) =
    match symbol with
    | :? FSharpEntity as entity -> ValueSome(definitionOf entity)
    | :? FSharpMemberOrFunctionOrValue as value when value.IsConstructor ->
        match value.DeclaringEntity with
        | Some entity -> ValueSome(definitionOf entity)
        | None -> ValueNone
    | _ -> ValueNone

let private canBeDerivedFrom (entity: FSharpEntity) =
    entity.IsInterface
    || entity.IsClass
       && not (
           entity.Attributes
           |> Seq.exists (fun attribute -> attribute.IsAttribute<SealedAttribute>())
       )

let private classify (symbol: FSharpSymbol) =
    match symbol with
    | :? FSharpEntity as entity ->
        match definitionOf entity with
        | definition when canBeDerivedFrom definition -> Target.DerivedTypes definition
        | _ -> Target.Self
    | :? FSharpMemberOrFunctionOrValue as value ->
        match value.DeclaringEntity with
        | Some declaringEntity when value.IsDispatchSlot -> Target.Slot(value, declaringEntity)
        | Some declaringEntity when value.IsOverrideOrExplicitInterfaceImplementation -> Target.Override(value, declaringEntity)
        | _ -> Target.Self
    | _ -> Target.Self

let private entityComparer =
    { new IEqualityComparer<FSharpEntity> with
        member _.Equals(left, right) = left.IsEffectivelySameAs right
        member _.GetHashCode entity = entity.GetEffectivelySameAsHash()
    }

/// The projects compiling the file that declares the symbol or, for a symbol from an assembly, referencing it.
let private declaringProjects (symbol: FSharpSymbol) (solution: Solution) =
    let inSolution =
        symbol.ImplementationLocation
        |> Option.orElse symbol.DeclarationLocation
        |> Option.map (fun range -> solution.GetDocumentIdsWithFSharpFileName range.FileName |> List.map _.ProjectId)
        |> Option.defaultValue []

    match inSolution, symbol.Assembly.FileName with
    | [], Some assemblyPath ->
        ProjectFiltering.getProjectsReferencingAssembly assemblyPath solution
        |> List.map _.Id
    | projectIds, _ -> projectIds

/// The F# projects a type or member can be implemented in: the declaring projects and every project depending on them.
let private projectsToSearch (symbol: FSharpSymbol) (solution: Solution) =
    let graph = solution.GetProjectDependencyGraph()
    let declaring = declaringProjects symbol solution
    let projectIds = HashSet declaring

    for projectId in declaring do
        projectIds.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject projectId)

    [
        for projectId in projectIds do
            match solution.GetProject projectId with
            | null -> ()
            | project when project.IsFSharp -> project
            | _ -> ()
    ]

let private implementationFiles (projects: Project list) =
    [|
        for project in projects do
            for document in project.Documents do
                if isFSharpSourceFile document.FilePath && not (isSignatureFile document.FilePath) then
                    document
    |]

let private symbolAt (checkResults: FSharpCheckFileResults) (sourceText: SourceText) (range: range) names =
    let lineText = sourceText.Lines[range.StartLine - 1].ToString()
    checkResults.GetSymbolUseAtLocation(range.StartLine, range.EndColumn, lineText, names)

/// Each check takes a turn on the budget every search shares; a document whose project is not loaded yet is skipped.
let private withCheckResults (document: Document) (work: FSharpCheckFileResults -> SourceText -> CancellableTask<unit>) =
    cancellableTask {
        let! cancellationToken = CancellableTask.getCancellationToken ()
        do! SymbolHelpers.searchThrottle.WaitAsync cancellationToken

        try
            match! document.TryGetFSharpParseAndCheckResultsAsync UserOpName with
            | ValueNone -> ()
            | ValueSome(struct (_, checkResults)) ->
                let! sourceText = document.GetTextAsync cancellationToken
                do! work checkResults sourceText
        finally
            SymbolHelpers.searchThrottle.Release() |> ignore
    }

let private implementationAt (document: Document) sourceText range (symbol: FSharpSymbol) name =
    RoslynHelpers.TryFSharpRangeToTextSpan(sourceText, range)
    |> ValueOption.map (fun span ->
        {
            Document = document
            Span = span
            Name = name
            Tags = FSharpGlyphTags.GetTags(Tokenizer.GetGlyphForSymbol(symbol, LexerSymbolKind.Ident))
        })

/// The type a site declares, when the type it names is one of the known ones.
let private confirm (known: ConcurrentDictionary<FSharpEntity, unit>) checkResults sourceText (site: InheritanceSite) =
    match symbolAt checkResults sourceText site.NameRange site.Names with
    | Some named when entityOf named.Symbol |> ValueOption.exists known.ContainsKey ->
        match symbolAt checkResults sourceText site.DeclaredNameRange [ site.DeclaredName ] with
        | Some declared -> entityOf declared.Symbol
        | None -> ValueNone
    | _ -> ValueNone

/// The type definitions and object expressions inheriting or implementing the target, directly or through the types
/// found before them, with the documents holding them. Each round looks for the names of the types the previous one
/// found, as DependentTypeFinder descends the inheritance tree; `onFound` hears of each site as it is confirmed.
let private findDerivedTypes
    (files: Document array)
    (target: FSharpEntity)
    (onFound: Document -> SourceText -> InheritanceSite -> FSharpEntity -> CancellableTask<unit>)
    =
    cancellableTask {
        let known = ConcurrentDictionary<FSharpEntity, unit>(entityComparer)
        known[target] <- ()

        let confirmed =
            ConcurrentDictionary<struct (string * range), struct (Document * InheritanceSite)>()

        // The target-framework instances of a project compile the same files: one of them examines a site per round.
        let examined = ConcurrentDictionary<struct (int * string * range), unit>()
        let mutable round = 0

        let mutable names =
            HashSet<string>([ target.DisplayNameCore ], StringComparer.Ordinal)

        while names.Count > 0 do
            let roundIndex = round
            let roundNames = names
            let found = ConcurrentQueue<string>()

            let examine (document: Document) =
                cancellableTask {
                    let! sites = InheritanceSites.getAsync document

                    let candidates =
                        [
                            for site in sites do
                                if
                                    roundNames.Contains site.SimpleName
                                    && not (confirmed.ContainsKey(struct (document.FilePath, site.NameRange)))
                                    && examined.TryAdd(struct (roundIndex, document.FilePath, site.NameRange), ())
                                then
                                    site
                        ]

                    if not candidates.IsEmpty then
                        do!
                            withCheckResults document (fun checkResults sourceText ->
                                cancellableTask {
                                    for site in candidates do
                                        match confirm known checkResults sourceText site with
                                        | ValueSome declared when
                                            confirmed.TryAdd(struct (document.FilePath, site.NameRange), struct (document, site))
                                            ->
                                            match site.Kind with
                                            | SiteKind.Abbreviation -> found.Enqueue site.DeclaredName
                                            | SiteKind.TypeDefinition ->
                                                if canBeDerivedFrom declared && known.TryAdd(declared, ()) then
                                                    found.Enqueue declared.DisplayNameCore

                                                do! onFound document sourceText site declared
                                            | SiteKind.ObjectExpression -> do! onFound document sourceText site declared
                                        | _ -> ()
                                })
                }

            do! CancellableTask.forEachThrottled Environment.ProcessorCount examine files
            round <- round + 1
            names <- HashSet<string>(found, StringComparer.Ordinal)

        return
            [
                for KeyValue(_, struct (document, site)) in confirmed do
                    if not site.Kind.IsAbbreviation then
                        struct (document, site)
            ]
    }

let private reportDerivedType (target: FSharpEntity) report document sourceText (site: InheritanceSite) (declared: FSharpEntity) =
    let name =
        match site.Kind with
        | SiteKind.ObjectExpression -> ValueSome $"new {site.DeclaredName}"
        | SiteKind.TypeDefinition when target.IsInterface && declared.IsInterface -> ValueNone
        | SiteKind.TypeDefinition
        | SiteKind.Abbreviation -> ValueSome declared.DisplayName

    match
        name
        |> ValueOption.bind (implementationAt document sourceText site.DeclaredNameRange declared)
    with
    | ValueSome implementation -> report implementation
    | ValueNone -> CancellableTask.singleton ()

let private displayNameOf (symbol: FSharpSymbol) =
    match symbol with
    | :? FSharpMemberOrFunctionOrValue as value ->
        match value.DeclaringEntity with
        | Some entity -> $"{entity.DisplayName}.{value.DisplayName}"
        | None -> value.DisplayName
    | symbol -> symbol.DisplayName

/// The members implementing the abstract members - the accessors of an abstract property among them - in the files of
/// the types deriving from their declaring type, and for a slot in the declaring type's file too. For an override, only
/// inside the derived types: the other implementations of what it implements are not its own.
let private findMemberImplementations
    (solution: Solution)
    (files: Document array)
    (slots: FSharpMemberOrFunctionOrValue list)
    (declaringEntity: FSharpEntity)
    (isOverride: bool)
    (report: Implementation -> CancellableTask<unit>)
    =
    cancellableTask {
        let slots =
            [
                for slot in slots do
                    slot

                    if slot.HasGetterMethod then
                        slot.GetterMethod

                    if slot.HasSetterMethod then
                        slot.SetterMethod
            ]

        let! derived = findDerivedTypes files declaringEntity (fun _ _ _ _ -> CancellableTask.singleton ())

        // Per file, the ranges of the derived types to look inside, or ValueNone for the whole file.
        let searches =
            Dictionary<string, struct (Document * range list voption)>(StringComparer.Ordinal)

        if not isOverride then
            let declaration =
                declaringEntity.ImplementationLocation
                |> Option.defaultValue declaringEntity.DeclarationLocation

            match solution.TryGetDocumentFromFSharpRange declaration with
            | Some document -> searches[document.FilePath] <- struct (document, ValueNone)
            | None -> ()

        for struct (document, site) in derived do
            match searches.TryGetValue document.FilePath with
            | true, struct (_, ValueNone) -> ()
            | true, struct (_, ValueSome ranges) -> searches[document.FilePath] <- struct (document, ValueSome(site.Range :: ranges))
            | false, _ when isOverride -> searches[document.FilePath] <- struct (document, ValueSome [ site.Range ])
            | false, _ -> searches[document.FilePath] <- struct (document, ValueNone)

        let search (document: Document) (within: range list voption) =
            withCheckResults document (fun checkResults sourceText ->
                cancellableTask {
                    let! cancellationToken = CancellableTask.getCancellationToken ()

                    let isInside (range: range) =
                        match within with
                        | ValueNone -> true
                        | ValueSome ranges ->
                            ranges
                            |> List.exists (fun derivedType -> Range.rangeContainsRange derivedType range)

                    let implemented =
                        [
                            for slot in slots do
                                for slotUse in checkResults.GetUsesOfSymbolInFile(slot, cancellationToken = cancellationToken) do
                                    if slotUse.IsFromDispatchSlotImplementation && isInside slotUse.Range then
                                        slotUse
                        ]

                    if not implemented.IsEmpty then
                        let definitions =
                            checkResults.GetAllUsesOfAllSymbolsInFile(cancellationToken)
                            |> Seq.filter _.IsFromDefinition
                            |> Seq.toArray

                        for slotUse in implemented do
                            let range = slotUse.Range

                            let struct (symbol, name) =
                                match
                                    definitions
                                    |> Array.tryFindV (fun definition -> Range.equals definition.Range range)
                                with
                                | ValueSome definition -> struct (definition.Symbol, displayNameOf definition.Symbol)
                                | ValueNone ->
                                    // A member of an object expression declares no symbol: it is named after the expression.
                                    match
                                        derived
                                        |> List.tryFindV (fun struct (objectExpression, site) ->
                                            site.Kind.IsObjectExpression
                                            && objectExpression.FilePath = document.FilePath
                                            && Range.rangeContainsRange site.Range range)
                                    with
                                    | ValueSome(struct (_, site)) ->
                                        struct (slotUse.Symbol, $"new {site.DeclaredName}.{slotUse.Symbol.DisplayName}")
                                    | ValueNone -> struct (slotUse.Symbol, displayNameOf slotUse.Symbol)

                            match implementationAt document sourceText range symbol name with
                            | ValueSome implementation -> do! report implementation
                            | ValueNone -> ()
                })

        do!
            [
                for KeyValue(_, struct (document, within)) in searches -> search document within
            ]
            |> CancellableTask.whenAll
            |> CancellableTask.ignore
    }

/// The abstract members an override implements: its check records a use of each at the override's name.
let private slotsImplementedBy (overriding: FSharpMemberOrFunctionOrValue) (solution: Solution) =
    cancellableTask {
        match overriding.ImplementationLocation with
        | None -> return []
        | Some range ->
            match solution.TryGetDocumentFromFSharpRange range with
            | None -> return []
            | Some document ->
                match! document.TryGetFSharpParseAndCheckResultsAsync UserOpName with
                | ValueNone -> return []
                | ValueSome(struct (_, checkResults)) ->
                    let! cancellationToken = CancellableTask.getCancellationToken ()

                    return
                        [
                            for symbolUse in checkResults.GetAllUsesOfAllSymbolsInFile(cancellationToken) do
                                match symbolUse.Symbol with
                                | :? FSharpMemberOrFunctionOrValue as slot when
                                    symbolUse.IsFromDispatchSlotImplementation && Range.equals symbolUse.Range range
                                    ->
                                    slot
                                | _ -> ()
                        ]
    }

let private isInterfaceTypeOrMember (symbol: ISymbol) =
    match symbol with
    | :? INamedTypeSymbol as namedType -> namedType.TypeKind = TypeKind.Interface
    | _ ->
        match symbol.ContainingType with
        | null -> false
        | containingType ->
            containingType.TypeKind = TypeKind.Interface
            && (symbol.IsAbstract || symbol.IsVirtual)

let private isOverridable (symbol: ISymbol) =
    match symbol.ContainingType with
    | null -> false
    | containingType ->
        containingType.TypeKind <> TypeKind.Interface
        && (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride)
        && not symbol.IsSealed

/// What implements or derives from the symbol in C# and Visual Basic source, found as Go To Implementation finds it
/// in C#.
let private roslynImplementationsAsync (symbol: ISymbol) (solution: Solution) (projects: IImmutableSet<Project>) =
    cancellableTask {
        let! cancellationToken = CancellableTask.getCancellationToken ()

        match symbol with
        | :? INamedTypeSymbol as namedType when namedType.TypeKind = TypeKind.Class ->
            let! derived = SymbolFinder.FindDerivedClassesAsync(namedType, solution, true, projects, cancellationToken)
            return [ for derivedClass in derived -> derivedClass :> ISymbol ]
        | _ when isInterfaceTypeOrMember symbol ->
            let! implementations = SymbolFinder.FindImplementationsAsync(symbol, solution, projects, cancellationToken)
            let found = ResizeArray implementations

            for implementation in implementations do
                if isOverridable implementation then
                    let! overrides = SymbolFinder.FindOverridesAsync(implementation, solution, projects, cancellationToken)
                    found.AddRange overrides

            return List.ofSeq found
        | :? INamedTypeSymbol -> return []
        | _ when isOverridable symbol ->
            let! overrides = SymbolFinder.FindOverridesAsync(symbol, solution, projects, cancellationToken)
            return List.ofSeq overrides
        | _ -> return []
    }

let private roslynGlyph (symbol: ISymbol) =
    match symbol with
    | :? INamedTypeSymbol as namedType ->
        match namedType.TypeKind with
        | TypeKind.Interface -> FSharpGlyph.InterfacePublic
        | TypeKind.Struct -> FSharpGlyph.StructurePublic
        | TypeKind.Delegate -> FSharpGlyph.DelegatePublic
        | TypeKind.Enum -> FSharpGlyph.EnumPublic
        | _ -> FSharpGlyph.ClassPublic
    | :? IPropertySymbol -> FSharpGlyph.PropertyPublic
    | :? IEventSymbol -> FSharpGlyph.EventPublic
    | _ -> FSharpGlyph.MethodPublic

let private roslynDisplayName (symbol: ISymbol) =
    match symbol with
    | :? INamedTypeSymbol -> symbol.Name
    | _ -> $"{symbol.ContainingType.Name}.{symbol.Name}"

/// The C# and Visual Basic implementations, in the projects referencing the assembly of an F# project searched or the
/// assembly the symbol comes from.
let private findInCompilationProjects
    (symbol: FSharpSymbol)
    (fsharpProjects: Project list)
    (solution: Solution)
    (report: Implementation -> CancellableTask<unit>)
    =
    cancellableTask {
        let referencing =
            [
                for project in fsharpProjects do
                    yield! ProjectFiltering.getCompilationProjectsReferencingOutputOf project

                match symbol.Assembly.FileName with
                | Some assemblyPath -> yield! ProjectFiltering.getCompilationProjectsReferencingAssembly assemblyPath solution
                | None -> ()
            ]
            |> List.distinctBy _.Id

        match symbol.DocumentationCommentId, referencing with
        | ValueSome docId, first :: _ when not symbol.IsInternalToProject ->
            let! cancellationToken = CancellableTask.getCancellationToken ()
            // Building a compilation and walking it costs the same cores the F# search is using.
            do! SymbolHelpers.searchThrottle.WaitAsync cancellationToken

            try
                match! first.GetCompilationAsync cancellationToken with
                | null -> ()
                | compilation ->
                    match DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation) with
                    | null -> ()
                    | roslynSymbol ->
                        let! found = roslynImplementationsAsync roslynSymbol solution (ImmutableHashSet.CreateRange referencing)

                        for implementation in found do
                            for location in implementation.Locations do
                                if location.IsInSource then
                                    match solution.GetDocument location.SourceTree with
                                    | null -> ()
                                    | document ->
                                        do!
                                            report
                                                {
                                                    Document = document
                                                    Span = location.SourceSpan
                                                    Name = roslynDisplayName implementation
                                                    Tags = FSharpGlyphTags.GetTags(roslynGlyph implementation)
                                                }
            finally
                SymbolHelpers.searchThrottle.Release() |> ignore
        | _ -> ()
    }

/// The member declared where an abstract member is used. At the name of an override or of an interface member's
/// implementation, name resolution answers the abstract member implemented there, while Go To Implementation is asked
/// about the member declared there.
let tryDeclaredAt (symbolUse: FSharpSymbolUse) (checkResults: FSharpCheckFileResults) (cancellationToken: Threading.CancellationToken) =
    match symbolUse.Symbol with
    | :? FSharpMemberOrFunctionOrValue as slot when slot.IsDispatchSlot ->
        checkResults.GetAllUsesOfAllSymbolsInFile(cancellationToken)
        |> Seq.tryFindV (fun declaration ->
            declaration.IsFromDefinition
            && Range.equals declaration.Range symbolUse.Range
            && not (declaration.Symbol.IsEffectivelySameAs slot))
        |> ValueOption.map _.Symbol
    | _ -> ValueNone

/// Reports what implements the symbol, each implementation once and as soon as it is found, and returns how many were
/// reported: when there are none, the caller shows the symbol itself, as Roslyn does.
let findAsync (symbol: FSharpSymbol) (document: Document) (report: Implementation -> CancellableTask<unit>) =
    cancellableTask {
        let reported = ConcurrentDictionary<struct (string * TextSpan), unit>()

        let reportOnce (implementation: Implementation) =
            if reported.TryAdd(struct (implementation.Document.FilePath, implementation.Span), ()) then
                report implementation
            else
                CancellableTask.singleton ()

        match classify symbol with
        | Target.Self -> ()
        | target ->
            let solution = document.Project.Solution

            // An abbreviation has no projects or C# symbol of its own: what it abbreviates does.
            let searched =
                match target with
                | Target.DerivedTypes entity -> entity :> FSharpSymbol
                | Target.Slot(value, _)
                | Target.Override(value, _) -> value :> FSharpSymbol
                | Target.Self -> symbol

            let projects = projectsToSearch searched solution
            let files = implementationFiles projects

            let fsharpSearch =
                match target with
                | Target.DerivedTypes derivedFrom ->
                    findDerivedTypes files derivedFrom (reportDerivedType derivedFrom reportOnce)
                    |> CancellableTask.map ignore
                | Target.Slot(slot, declaringEntity) -> findMemberImplementations solution files [ slot ] declaringEntity false reportOnce
                | Target.Override(overriding, declaringEntity) ->
                    cancellableTask {
                        match! slotsImplementedBy overriding solution with
                        | [] -> ()
                        | slots -> do! findMemberImplementations solution files slots declaringEntity true reportOnce
                    }
                | Target.Self -> CancellableTask.singleton ()

            do!
                [
                    fsharpSearch
                    findInCompilationProjects searched projects solution reportOnce
                ]
                |> CancellableTask.whenAll
                |> CancellableTask.ignore

        return reported.Count
    }
