// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Collections.Generic
open System.Composition

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CodeActions
open Microsoft.CodeAnalysis.CodeRefactorings
open Microsoft.CodeAnalysis.Formatting
open Microsoft.CodeAnalysis.Text
open Microsoft.VisualStudio.FSharp.Editor.Telemetry

open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTrivia
open FSharp.Compiler.Text

open CancellableTasks

[<RequireQualifiedAccess>]
module private NamespaceModuleConversion =

    [<NoComparison; NoEquality>]
    type Shape =
        | Nested of
            namespacePath: LongIdent *
            namespaceIsRecursive: bool *
            namespaceKeyword: range *
            moduleIdent: Ident *
            moduleIsRecursive: bool *
            moduleKeyword: range *
            equals: range *
            bodyColumn: int
        | Root of path: LongIdent * keyword: range * moduleRange: range

    let spanOf (sourceText: SourceText) (m: range) =
        RoslynHelpers.FSharpRangeToTextSpan(sourceText, m)

    let textBetween (sourceText: SourceText) (first: Ident) (last: Ident) =
        sourceText.ToString(TextSpan.FromBounds((spanOf sourceText first.idRange).Start, (spanOf sourceText last.idRange).End))

    let isBlank (sourceText: SourceText) start finish =
        String.IsNullOrWhiteSpace(sourceText.ToString(TextSpan.FromBounds(start, finish)))

    let restOfLineIsBlank (sourceText: SourceText) (m: range) =
        let position = (spanOf sourceText m).End
        isBlank sourceText position (sourceText.Lines.GetLineFromPosition position).End

    let tryShape (sourceText: SourceText) (parseTree: ParsedInput) =
        match parseTree with
        | ParsedInput.ImplFile(ParsedImplFileInput(
            contents = [ SynModuleOrNamespace(
                             longId = namespacePath
                             isRecursive = namespaceIsRecursive
                             kind = SynModuleOrNamespaceKind.DeclaredNamespace
                             decls = [ SynModuleDecl.NestedModule(
                                           moduleInfo = moduleInfo
                                           isRecursive = moduleIsRecursive
                                           decls = (firstDeclaration :: _ as declarations)
                                           range = moduleRange
                                           trivia = moduleTrivia) ]
                             trivia = namespaceTrivia) ])) ->
            match namespaceTrivia.LeadingKeyword, moduleInfo.LongIdent, moduleTrivia.ModuleKeyword, moduleTrivia.EqualsRange with
            | SynModuleOrNamespaceLeadingKeyword.Namespace namespaceKeyword, [ moduleIdent ], Some moduleKeyword, Some equals when
                namespaceKeyword.StartColumn = 0
                && moduleKeyword.StartColumn = 0
                && moduleKeyword.StartLine = equals.EndLine
                && firstDeclaration.Range.StartLine > equals.EndLine
                && Position.posEq moduleRange.End (List.last declarations).Range.End
                && restOfLineIsBlank sourceText (List.last namespacePath).idRange
                ->
                ValueSome(
                    Nested(
                        namespacePath,
                        namespaceIsRecursive,
                        namespaceKeyword,
                        moduleIdent,
                        moduleIsRecursive,
                        moduleKeyword,
                        equals,
                        firstDeclaration.Range.StartColumn
                    )
                )
            | _ -> ValueNone

        | ParsedInput.ImplFile(ParsedImplFileInput(
            contents = [ SynModuleOrNamespace(
                             longId = (_ :: _ :: _ as path)
                             kind = SynModuleOrNamespaceKind.NamedModule
                             decls = firstDeclaration :: _
                             range = moduleRange
                             trivia = moduleTrivia) ])) ->
            match moduleTrivia.LeadingKeyword with
            | SynModuleOrNamespaceLeadingKeyword.Module keyword when
                keyword.StartColumn = 0
                && (List.last path).idRange.EndLine = keyword.StartLine
                && firstDeclaration.Range.StartLine > keyword.StartLine
                ->
                ValueSome(Root(path, keyword, moduleRange))
            | _ -> ValueNone

        | _ -> ValueNone

    let isOnHeader (caretLine: int) shape =
        match shape with
        | Nested(namespaceKeyword = namespaceKeyword; moduleKeyword = moduleKeyword) ->
            caretLine = namespaceKeyword.StartLine || caretLine = moduleKeyword.StartLine
        | Root(keyword = keyword) -> caretLine = keyword.StartLine

    let dotted (idents: LongIdent) =
        idents |> List.map _.idText |> String.concat "."

    let title shape =
        match shape with
        | Nested(namespacePath = namespacePath; moduleIdent = moduleIdent) ->
            String.Format(SR.ConvertToRootModule(), $"{dotted namespacePath}.{moduleIdent.idText}")
        | Root(path = path) ->
            String.Format(SR.ConvertToNamespaceWithNestedModule(), dotted (List.take (path.Length - 1) path), (List.last path).idText)

    let linesInsideLiterals (parseTree: ParsedInput) =
        (HashSet<int>(), parseTree)
        ||> ParsedInput.fold (fun lines _ node ->
            match node with
            | SyntaxNode.SynExpr(SynExpr.Const(range = m))
            | SyntaxNode.SynExpr(SynExpr.InterpolatedString(range = m))
            | SyntaxNode.SynPat(SynPat.Const(range = m)) ->
                for line in m.StartLine + 1 .. m.EndLine do
                    lines.Add(Line.toZ line) |> ignore
            | _ -> ()

            lines)

    let leadingSpaces (sourceText: SourceText) (line: TextLine) =
        let mutable position = line.Start

        while position < line.End && sourceText[position] = ' ' do
            position <- position + 1

        position - line.Start

    let lineBreakOf (sourceText: SourceText) (line: TextLine) =
        match line.EndIncludingLineBreak - line.End with
        | 0 -> Environment.NewLine
        | length -> sourceText.ToString(TextSpan(line.End, length))

    let changes (sourceText: SourceText) (parseTree: ParsedInput) (indentSize: int) shape =
        let lines = sourceText.Lines
        let literalLines = linesInsideLiterals parseTree

        match shape with
        | Nested(namespacePath, namespaceIsRecursive, namespaceKeyword, moduleIdent, moduleIsRecursive, _, equals, bodyColumn) ->
            let namespaceLine = Line.toZ namespaceKeyword.StartLine
            let headerLine = Line.toZ equals.EndLine

            let firstKeptLine =
                seq { namespaceLine + 1 .. headerLine }
                |> Seq.find (fun i -> not (String.IsNullOrWhiteSpace(lines[i].ToString())))

            let moduleSpan = spanOf sourceText moduleIdent.idRange
            let equalsEnd = (spanOf sourceText equals).End

            let headerEnd =
                if isBlank sourceText equalsEnd lines[headerLine].End then
                    lines[headerLine].End
                else
                    equalsEnd

            let recursive =
                if namespaceIsRecursive && not moduleIsRecursive then
                    "rec "
                else
                    ""

            let rootPath =
                $"{recursive}{textBetween sourceText namespacePath.Head (List.last namespacePath)}.{sourceText.ToString moduleSpan}"

            [
                TextChange(TextSpan.FromBounds(lines[namespaceLine].Start, lines[firstKeptLine].Start), "")
                TextChange(TextSpan.FromBounds(moduleSpan.Start, headerEnd), rootPath)

                for i in headerLine + 1 .. lines.Count - 1 do
                    let removed = min bodyColumn (leadingSpaces sourceText lines[i])

                    if removed > 0 && not (literalLines.Contains i) then
                        TextChange(TextSpan(lines[i].Start, removed), "")
            ]

        | Root(path, keyword, moduleRange) ->
            let lastIdent = List.last path
            let namespaceIdents = List.take (path.Length - 1) path
            let lineBreak = lineBreakOf sourceText lines[Line.toZ keyword.StartLine]
            let lastSpan = spanOf sourceText lastIdent.idRange
            let padding = String(' ', indentSize)

            [
                TextChange(
                    TextSpan(lines[Line.toZ moduleRange.StartLine].Start, 0),
                    $"namespace {textBetween sourceText path.Head (List.last namespaceIdents)}{lineBreak}{lineBreak}"
                )
                TextChange(
                    TextSpan.FromBounds((spanOf sourceText path.Head.idRange).Start, lastSpan.End),
                    $"{sourceText.ToString lastSpan} ="
                )

                for i in Line.toZ keyword.StartLine + 1 .. lines.Count - 1 do
                    if not (String.IsNullOrWhiteSpace(lines[i].ToString()) || literalLines.Contains i) then
                        TextChange(TextSpan(lines[i].Start, 0), padding)
            ]

[<ExportCodeRefactoringProvider(FSharpConstants.FSharpLanguageName, Name = "ConvertNamespaceModule"); Shared>]
type internal FSharpConvertNamespaceModuleRefactoring [<ImportingConstructor>] () =
    inherit CodeRefactoringProvider()

    static let hasSignatureFile (document: Document) =
        let signaturePath = document.FilePath + "i"

        document.Project.Documents
        |> Seq.exists (fun d -> String.Equals(d.FilePath, signaturePath, StringComparison.OrdinalIgnoreCase))

    override _.ComputeRefactoringsAsync context =
        cancellableTask {
            let document = context.Document

            if not (document.IsFSharpSignatureFile || document.IsFSharpScript) then
                let! cancellationToken = CancellableTask.getCancellationToken ()
                let! sourceText = document.GetTextAsync cancellationToken
                let! parseResults = document.GetFSharpParseResultsAsync(nameof FSharpConvertNamespaceModuleRefactoring)

                let caretLine =
                    Line.fromZ (sourceText.Lines.GetLineFromPosition context.Span.Start).LineNumber

                match NamespaceModuleConversion.tryShape sourceText parseResults.ParseTree with
                | ValueSome shape when
                    NamespaceModuleConversion.isOnHeader caretLine shape
                    && not (hasSignatureFile document)
                    ->
                    let title = NamespaceModuleConversion.title shape

                    let changedDocument =
                        cancellableTask {
                            let! cancellationToken = CancellableTask.getCancellationToken ()
                            let! options = document.GetOptionsAsync cancellationToken

                            let indentSize =
                                options.GetOption(FormattingOptions.IndentationSize, FSharpConstants.FSharpLanguageName)

                            TelemetryReporter.ReportSingleEvent(
                                TelemetryEvents.RefactoringActivated,
                                [| "name", box (nameof FSharpConvertNamespaceModuleRefactoring) |]
                            )

                            let changes =
                                NamespaceModuleConversion.changes sourceText parseResults.ParseTree indentSize shape

                            return document.WithText(sourceText.WithChanges changes)
                        }

                    context.RegisterRefactoring(CodeAction.Create(title, changedDocument, title))
                | _ -> ()
        }
        |> CancellableTask.startAsTask context.CancellationToken
