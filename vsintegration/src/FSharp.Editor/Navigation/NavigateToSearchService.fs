// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.IO
open System.Composition
open System.Collections.Immutable
open System.Collections.Concurrent
open System.Globalization
open System.Linq
open System.Threading.Tasks

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.Navigation
open Microsoft.CodeAnalysis.ExternalAccess.FSharp.NavigateTo
open Microsoft.VisualStudio.LanguageServices
open Microsoft.VisualStudio.Text.PatternMatching

open FSharp.Compiler.EditorServices
open FSharp.Compiler.Syntax
open CancellableTasks

/// Parse-tree navigable items per document, cached on the document's text version.
/// Shared by NavigateTo and by the Copilot chat mention provider.
[<Export; Shared>]
type internal FSharpNavigableItemsCache
    [<ImportingConstructor>]
    (patternMatcherFactory: IPatternMatcherFactory, [<Import(AllowDefault = true)>] workspace: VisualStudioWorkspace) =

    let cache =
        ConcurrentDictionary<
            DocumentId,
            struct {|
                Version: VersionStamp
                Approximate: bool
                Items: NavigableItem array
            |}
         >()

    /// Whether the file's parse depends on the defines, by file path: known once any instance has parsed it.
    let conditionalDirectives =
        ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

    do
        match workspace with
        | null -> ()
        | workspace ->
            workspace.WorkspaceChanged.Add(fun e ->
                if e.NewSolution.Id <> e.OldSolution.Id then
                    cache.Clear()
                    conditionalDirectives.Clear())

    let hasConditionalDirectives (parseTree: ParsedInput) =
        match parseTree with
        | ParsedInput.ImplFile file -> not file.Trivia.ConditionalDirectives.IsEmpty
        | ParsedInput.SigFile file -> not file.Trivia.ConditionalDirectives.IsEmpty

    let store (document: Document) version approximate (parseTree: ParsedInput) =
        let items = NavigateTo.GetNavigableItems parseTree

        cache[document.Id] <-
            struct {|
                Version = version
                Approximate = approximate
                Items = items
            |}

        match document.FilePath with
        | null -> ()
        | path -> conditionalDirectives[path] <- hasConditionalDirectives parseTree

        items

    member _.GetNavigableItems(document: Document) =
        cancellableTask {
            let! ct = CancellableTask.getCancellationToken ()
            let! currentVersion = document.GetTextVersionAsync(ct)

            match cache.TryGetValue document.Id with
            | true, entry when entry.Version = currentVersion && not entry.Approximate -> return entry.Items
            | _ ->
                let! parseResults = document.GetFSharpParseResultsAsync(nameof (FSharpNavigableItemsCache))
                return store document currentVersion false parseResults.ParseTree
        }

    /// The items of a parse that does not wait for the project's compilation options, for the search that runs while
    /// the solution is still loading. A file behind `#if` can be read under the wrong defines, so the entry it leaves
    /// behind never answers `GetNavigableItems`.
    member _.GetNavigableItemsWhileLoading(document: Document) =
        cancellableTask {
            let! ct = CancellableTask.getCancellationToken ()
            let! currentVersion = document.GetTextVersionAsync(ct)

            match cache.TryGetValue document.Id with
            | true, entry when entry.Version = currentVersion -> return entry.Items
            | _ ->
                let! parseResults = document.GetFSharpQuickParseResultsAsync(nameof (FSharpNavigableItemsCache))
                return store document currentVersion true parseResults.ParseTree
        }

    /// The items of the document's last parse, whatever version they came from. Reads no text, so a
    /// closed document costs nothing; a caller that needs the items of the current text asks for them.
    member _.TryGetCachedNavigableItems(documentId: DocumentId) =
        match cache.TryGetValue documentId with
        | true, entry -> ValueSome entry.Items
        | _ -> ValueNone

    /// Whether the file's parse tree, from whichever instance parsed it first, has `#if` directives.
    /// `true` when unknown, so a file no instance has parsed yet is searched everywhere.
    member _.HasConditionalDirectives(filePath: string) =
        match conditionalDirectives.TryGetValue filePath with
        | true, dependsOnDefines -> dependsOnDefines
        | _ -> true

    member _.CreateMatcherFor(searchPattern: string) =
        let patternMatcher =
            patternMatcherFactory.CreatePatternMatcher(
                searchPattern,
                PatternMatcherCreationOptions(
                    cultureInfo = CultureInfo.CurrentUICulture,
                    flags = PatternMatcherCreationFlags.AllowFuzzyMatching,
                    containerSplitCharacters = [ '.' ]
                )
            )

        fun (item: NavigableItem) ->
            // PatternMatcher will not match operators and some backtick escaped identifiers.
            // To handle them, we fall back to simple substring match.
            let name = item.Name

            if item.NeedsBackticks then
                match name.IndexOf(searchPattern, StringComparison.CurrentCultureIgnoreCase) with
                | i when i > 0 -> ValueSome(PatternMatch(PatternMatchKind.Substring, false, false))
                | 0 when name.Length = searchPattern.Length -> ValueSome(PatternMatch(PatternMatchKind.Exact, false, false))
                | 0 -> ValueSome(PatternMatch(PatternMatchKind.Prefix, false, false))
                | _ -> ValueNone
            else
                // full name with dots allows for path matching, e.g.
                // "f.c.so.elseif" will match "Fantomas.Core.SyntaxOak.ElseIfNode"
                patternMatcher.TryMatch $"{item.Container.FullName}.{name}"
                |> ValueOption.ofNullable

[<Export(typeof<IFSharpNavigateToSearchService>); Shared>]
type internal FSharpNavigateToSearchService [<ImportingConstructor>] (itemsCache: FSharpNavigableItemsCache) =

    /// A multi-targeted project is one Roslyn project per target framework over the same files. The
    /// first instance in the solution searches every file; the others only the files they alone compile
    /// and the files whose parse depends on the defines.
    let searchedIn (project: Project) =
        match project.FilePath with
        | null -> fun (_: Document) -> true
        | projectPath ->
            let instances =
                project.Solution.Projects
                |> Seq.filter (fun p -> p.FilePath = projectPath)
                |> Seq.map _.Id
                |> Seq.toArray

            fun (document: Document) ->
                match document.FilePath with
                | null -> true
                | path ->
                    let documentIds = project.Solution.GetDocumentIdsWithFilePath path

                    let owner =
                        instances
                        |> Array.find (fun id -> documentIds |> Seq.exists (fun documentId -> documentId.ProjectId = id))

                    owner = project.Id || itemsCache.HasConditionalDirectives path

    let kindsProvided =
        ImmutableHashSet.Create(
            FSharpNavigateToItemKind.Module,
            FSharpNavigateToItemKind.Class,
            FSharpNavigateToItemKind.Field,
            FSharpNavigateToItemKind.Property,
            FSharpNavigateToItemKind.Method,
            FSharpNavigateToItemKind.Enum,
            FSharpNavigateToItemKind.EnumItem
        )

    let navigateToItemKindToRoslynKind =
        function
        | NavigableItemKind.Module -> FSharpNavigateToItemKind.Module
        | NavigableItemKind.ModuleAbbreviation -> FSharpNavigateToItemKind.Module
        | NavigableItemKind.Exception -> FSharpNavigateToItemKind.Class
        | NavigableItemKind.Type -> FSharpNavigateToItemKind.Class
        | NavigableItemKind.ModuleValue -> FSharpNavigateToItemKind.Field
        | NavigableItemKind.Field -> FSharpNavigateToItemKind.Field
        | NavigableItemKind.Property -> FSharpNavigateToItemKind.Property
        | NavigableItemKind.Constructor -> FSharpNavigateToItemKind.Method
        | NavigableItemKind.Member -> FSharpNavigateToItemKind.Method
        | NavigableItemKind.EnumCase -> FSharpNavigateToItemKind.EnumItem
        | NavigableItemKind.UnionCase -> FSharpNavigateToItemKind.EnumItem

    let navigateToItemKindToGlyph =
        function
        | NavigableItemKind.Module -> Glyph.ModulePublic
        | NavigableItemKind.ModuleAbbreviation -> Glyph.ModulePublic
        | NavigableItemKind.Exception -> Glyph.ClassPublic
        | NavigableItemKind.Type -> Glyph.ClassPublic
        | NavigableItemKind.ModuleValue -> Glyph.FieldPublic
        | NavigableItemKind.Field -> Glyph.FieldPublic
        | NavigableItemKind.Property -> Glyph.PropertyPublic
        | NavigableItemKind.Constructor -> Glyph.MethodPublic
        | NavigableItemKind.Member -> Glyph.MethodPublic
        | NavigableItemKind.EnumCase -> Glyph.EnumPublic
        | NavigableItemKind.UnionCase -> Glyph.EnumPublic

    let formatInfo (container: NavigableContainer) (document: Document) =
        let projectName = document.Project.Name

        let description =
            match container.Type with
            | NavigableContainerType.File -> $"{Path.GetFileName container.Name} - project {projectName}"
            | NavigableContainerType.Exception
            | NavigableContainerType.Type -> $"in {container.Name} - project {projectName}"
            | NavigableContainerType.Module -> $"module {container.Name} - project {projectName}"
            | NavigableContainerType.Namespace -> $"{container.FullName} - project {projectName}" // or maybe show only project name?

        if document.IsFSharpSignatureFile then
            $"signature, {description}"
        else
            description

    let patternMatchKindToNavigateToMatchKind =
        function
        | PatternMatchKind.Exact -> FSharpNavigateToMatchKind.Exact
        | PatternMatchKind.Prefix -> FSharpNavigateToMatchKind.Prefix
        | PatternMatchKind.Substring -> FSharpNavigateToMatchKind.Substring
        | PatternMatchKind.CamelCaseExact -> FSharpNavigateToMatchKind.CamelCaseExact
        | PatternMatchKind.CamelCasePrefix -> FSharpNavigateToMatchKind.CamelCasePrefix
        | PatternMatchKind.CamelCaseNonContiguousPrefix -> FSharpNavigateToMatchKind.CamelCaseNonContiguousPrefix
        | PatternMatchKind.CamelCaseSubstring -> FSharpNavigateToMatchKind.CamelCaseSubstring
        | PatternMatchKind.CamelCaseNonContiguousSubstring -> FSharpNavigateToMatchKind.CamelCaseNonContiguousSubstring
        | PatternMatchKind.Fuzzy -> FSharpNavigateToMatchKind.Fuzzy
        | _ -> FSharpNavigateToMatchKind.None

    let createMatcherFor (searchPattern: string) =
        itemsCache.CreateMatcherFor searchPattern

    let processDocument
        (getItems: Document -> CancellableTask<NavigableItem array>)
        (tryMatch: NavigableItem -> PatternMatch voption)
        (kinds: IImmutableSet<string>)
        (document: Document)
        =
        cancellableTask {
            let! items = getItems document

            let matches =
                [|
                    for item in items do
                        if kinds.Contains(navigateToItemKindToRoslynKind item.Kind) then
                            match tryMatch item with
                            | ValueSome m -> yield struct (item, m)
                            | ValueNone -> ()
                |]

            // The text, read from disk for a closed document, is only needed to place the matches.
            if matches.Length = 0 then
                return ImmutableArray.Empty
            else
                let! ct = CancellableTask.getCancellationToken ()
                let! sourceText = document.GetTextAsync ct

                return
                    [|
                        for struct (item, m) in matches do
                            match RoslynHelpers.TryFSharpRangeToTextSpan(sourceText, item.Range) with
                            | ValueNone -> ()
                            | ValueSome sourceSpan ->
                                yield
                                    FSharpNavigateToSearchResult(
                                        formatInfo item.Container document,
                                        navigateToItemKindToRoslynKind item.Kind,
                                        patternMatchKindToNavigateToMatchKind m.Kind,
                                        item.Name,
                                        FSharpNavigableItem(
                                            navigateToItemKindToGlyph item.Kind,
                                            ImmutableArray.Create(TaggedText(TextTags.Text, item.Name)),
                                            document,
                                            sourceSpan
                                        )
                                    )
                    |]
                    |> ImmutableArray.CreateRange
        }

    let searchProject (project: Project) searchPattern kinds =
        cancellableTask {
            let tryMatch = createMatcherFor searchPattern

            let! results =
                project.Documents
                |> Seq.filter (searchedIn project)
                |> Seq.map (processDocument itemsCache.GetNavigableItems tryMatch kinds)
                // Throttle to avoid launching a parse per document in the project all at once.
                |> CancellableTask.whenAllThrottled (max 1 Environment.ProcessorCount)

            return results |> Seq.collect _.AsEnumerable() |> Seq.toImmutableArray
        }

    /// Priority items first, each half in its original order, as NavigateTo's own service orders its work.
    let prioritize isPriority items =
        let priority, rest = items |> Seq.toArray |> Array.partition isPriority
        [| yield! priority; yield! rest |]

    /// A parse behind the loading search takes a turn on the budget every search shares.
    let throttled (work: CancellableTask<'a>) =
        cancellableTask {
            let! ct = CancellableTask.getCancellationToken ()
            do! SymbolHelpers.searchThrottle.WaitAsync ct

            try
                return! work
            finally
                SymbolHelpers.searchThrottle.Release() |> ignore
        }

    interface IFSharpNavigateToSearchService with
        member _.SearchProjectAsync
            (project, _priorityDocuments, searchPattern, kinds, cancellationToken)
            : Task<ImmutableArray<FSharpNavigateToSearchResult>> =
            searchProject project searchPattern kinds
            |> CancellableTask.start cancellationToken

        member _.SearchDocumentAsync(document: Document, searchPattern, kinds, cancellationToken) =
            processDocument itemsCache.GetNavigableItems (createMatcherFor searchPattern) kinds document cancellationToken

        member _.KindsProvided = kindsProvided

        member _.CanFilter = true

    interface IFSharpAdvancedNavigateToSearchService with
        member _.SearchCachedDocumentsAsync
            (
                _solution,
                projects,
                priorityDocuments,
                searchPattern,
                kinds,
                _activeDocument,
                onResultsFound,
                onProjectCompleted,
                cancellationToken
            ) : Task =
            let tryMatch = createMatcherFor searchPattern
            let priorityIds = ImmutableHashSet.CreateRange(priorityDocuments |> Seq.map _.Id)
            let isPriority (document: Document) = priorityIds.Contains document.Id

            let searchDocumentWhileLoading document =
                cancellableTask {
                    let! results = throttled (processDocument itemsCache.GetNavigableItemsWhileLoading tryMatch kinds document)

                    if results.Length > 0 then
                        do! onResultsFound.Invoke results
                }

            let searchProjectWhileLoading (project: Project) =
                cancellableTask {
                    do!
                        project.Documents
                        |> Seq.filter (searchedIn project)
                        |> prioritize isPriority
                        |> CancellableTask.forEachThrottled Environment.ProcessorCount searchDocumentWhileLoading

                    do! onProjectCompleted.Invoke()
                }

            projects
            |> prioritize (fun project -> project.Documents |> Seq.exists isPriority)
            |> Seq.map searchProjectWhileLoading
            |> CancellableTask.whenAll
            |> CancellableTask.startAsTask cancellationToken
