// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Where a file names a type it inherits, implements or abbreviates, read from its parse tree alone. Go To Implementation
/// looks candidates up by that name and confirms them against the check results, as Roslyn's DependentTypeFinder
/// confirms the inheritance names of its syntax index.
module internal Microsoft.VisualStudio.FSharp.Editor.InheritanceSites

open System.Collections.Concurrent

open Microsoft.CodeAnalysis

open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open CancellableTasks

[<RequireQualifiedAccess>]
type SiteKind =
    /// `inherit` or `interface` in a type definition.
    | TypeDefinition
    /// The type of an object expression, or an interface it adds.
    | ObjectExpression
    /// The right-hand side of a type abbreviation.
    | Abbreviation

[<Struct>]
type InheritanceSite =
    {
        Kind: SiteKind
        /// The identifiers written for the named type, generic arguments left out.
        Names: string list
        /// The range of the last of those identifiers.
        NameRange: range
        /// The declared type's name; for an object expression, the name of its type.
        DeclaredName: string
        DeclaredNameRange: range
        /// The type definition or object expression, members included.
        Range: range
    }

    member this.SimpleName = List.last this.Names

let rec private identsOf (synType: SynType) =
    match synType with
    | SynType.LongIdent(SynLongIdent(id = idents))
    | SynType.LongIdentApp(longDotId = SynLongIdent(id = idents)) -> idents
    | SynType.App(typeName = typeName) -> identsOf typeName
    | _ -> []

let private addSite (sites: ResizeArray<InheritanceSite>) kind (declared: Ident) range (synType: SynType) =
    match identsOf synType with
    | [] -> ()
    | idents ->
        sites.Add
            {
                Kind = kind
                Names = idents |> List.map _.idText
                NameRange = (List.last idents).idRange
                DeclaredName = declared.idText
                DeclaredNameRange = declared.idRange
                Range = range
            }

let private addTypeDefinitionSites sites (SynTypeDefn(typeInfo = typeInfo; typeRepr = typeRepr; members = members; range = range)) =
    match List.tryLast typeInfo.LongIdent with
    | None -> ()
    | Some declared ->
        let addInherited (memberDefn: SynMemberDefn) =
            match memberDefn with
            | SynMemberDefn.Inherit(baseType = Some inherited)
            | SynMemberDefn.ImplicitInherit(inheritType = inherited)
            | SynMemberDefn.Interface(interfaceType = inherited) -> addSite sites SiteKind.TypeDefinition declared range inherited
            | _ -> ()

        match typeRepr with
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.TypeAbbrev(rhsType = abbreviated), _) ->
            addSite sites SiteKind.Abbreviation declared range abbreviated
        | SynTypeDefnRepr.ObjectModel(members = reprMembers) -> List.iter addInherited reprMembers
        | _ -> ()

        List.iter addInherited members

let private addObjectExpressionSites sites (objType: SynType) (extraImpls: SynInterfaceImpl list) range =
    match List.tryLast (identsOf objType) with
    | None -> ()
    | Some declared ->
        addSite sites SiteKind.ObjectExpression declared range objType

        for SynInterfaceImpl(interfaceTy = interfaceType) in extraImpls do
            addSite sites SiteKind.ObjectExpression declared range interfaceType

/// The sites of a parse. A signature file repeats what its implementation file declares, so it has none.
let ofParseTree (parseTree: ParsedInput) =
    match parseTree with
    | ParsedInput.SigFile _ -> Array.empty
    | ParsedInput.ImplFile _ ->
        let sites =
            (ResizeArray(), parseTree)
            ||> ParsedInput.fold (fun sites _ node ->
                match node with
                | SyntaxNode.SynTypeDefn typeDefn -> addTypeDefinitionSites sites typeDefn
                | SyntaxNode.SynExpr(SynExpr.ObjExpr(objType = objType; extraImpls = extraImpls; range = range)) ->
                    addObjectExpressionSites sites objType extraImpls range
                | _ -> ()

                sites)

        if sites.Count = 0 then Array.empty else sites.ToArray()

/// Where the sites of a parse are kept, as the Navigate To cache keeps its items: under the defines it was parsed with,
/// or under `AnyDefines` when its tree holds no conditional directives and so reads the same under any of them.
[<Struct>]
type private SitesKey = { Defines: string; FilePath: string }

/// The key for a parse that does not depend on the defines. Not a define set any instance can have, since defines are
/// identifiers.
[<Literal>]
let private AnyDefines = "?"

let private cache =
    ConcurrentDictionary<SitesKey, struct (VersionStamp * InheritanceSite array)>()

let mutable private cachedSolution: SolutionId = null

let private dependsOnDefines (parseTree: ParsedInput) =
    match parseTree with
    | ParsedInput.ImplFile file -> not file.Trivia.ConditionalDirectives.IsEmpty
    | ParsedInput.SigFile file -> not file.Trivia.ConditionalDirectives.IsEmpty

let private definesOf (document: Document) =
    document.GetFSharpQuickDefines() |> String.concat ";"

/// The sites of the document's parse, kept per file and text version until another solution is searched. A document
/// whose project has no options yet has none.
let getAsync (document: Document) =
    cancellableTask {
        let solution = document.Project.Solution.Id

        if solution <> cachedSolution then
            cache.Clear()
            cachedSolution <- solution

        let! cancellationToken = CancellableTask.getCancellationToken ()
        let! version = document.GetTextVersionAsync cancellationToken

        let cached defines =
            match
                cache.TryGetValue(
                    {
                        Defines = defines
                        FilePath = document.FilePath
                    }
                )
            with
            | true, struct (cachedVersion, sites) when cachedVersion = version -> ValueSome sites
            | _ -> ValueNone

        match
            cached AnyDefines
            |> ValueOption.orElseWith (fun () -> cached (definesOf document))
        with
        | ValueSome sites -> return sites
        | ValueNone ->
            match! document.TryGetFSharpParseResultsAsync(nameof InheritanceSite) with
            | ValueNone -> return Array.empty
            | ValueSome parseResults ->
                let parseTree = parseResults.ParseTree
                let sites = ofParseTree parseTree

                let defines =
                    if dependsOnDefines parseTree then
                        definesOf document
                    else
                        AnyDefines

                cache[{
                          Defines = defines
                          FilePath = document.FilePath
                      }] <- struct (version, sites)

                return sites
    }
