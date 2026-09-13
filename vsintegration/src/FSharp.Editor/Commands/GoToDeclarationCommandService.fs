// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.ComponentModel.Composition

open Microsoft.CodeAnalysis.Text
open Microsoft.VisualStudio
open Microsoft.VisualStudio.Editor
open Microsoft.VisualStudio.OLE.Interop
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.TextManager.Interop
open Microsoft.VisualStudio.Threading
open Microsoft.VisualStudio.Utilities

open CancellableTasks

/// Go To Declaration, which Roslyn implements for no language: the declaration in the signature file when there is one,
/// otherwise the definition.
type internal GoToDeclarationCommandFilter(textView: IWpfTextView, gtd: GoToDefinition) =

    let mutable nextTarget: IOleCommandTarget = null

    let isGoToDeclaration (commandGroup: Guid) (commandId: uint32) =
        commandGroup = VSConstants.GUID_VSStandardCommandSet97
        && commandId = uint32 VSConstants.VSStd97CmdID.GotoDecl

    /// The command arrives on the main thread and nothing waits for it: the search runs in the background, and only
    /// opening the document comes back.
    let navigateToDeclaration () =
        let position = textView.Caret.Position.BufferPosition.Position

        match textView.TextBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges() with
        | null -> ()
        | document ->
            let navigation =
                ThreadHelper.JoinableTaskFactory.RunAsync(fun () ->
                    cancellableTask {
                        let! cancellationToken = CancellableTask.getCancellationToken ()

                        match! gtd.FindDeclarationAtPosition(document, position) with
                        | ValueSome(FSharpGoToDefinitionResult.NavigableItem item, _) ->
                            gtd.NavigateToItem(item, cancellationToken) |> ignore
                        | ValueSome(FSharpGoToDefinitionResult.ExternalAssembly(symbolUse, metadataReferences), _) ->
                            do!
                                gtd.NavigateToExternalDeclarationAsync(symbolUse, metadataReferences)
                                |> CancellableTask.ignore
                        | ValueNone -> ()
                    }
                    |> CancellableTask.startAsTaskWithoutCancellation)

            navigation.FileAndForget "fsharp/goToDeclaration"

    member this.AttachToViewAdapter(viewAdapter: IVsTextView) =
        match viewAdapter.AddCommandFilter this with
        | VSConstants.S_OK, next -> nextTarget <- next
        | errorCode, _ -> ErrorHandler.ThrowOnFailure errorCode |> ignore

    interface IOleCommandTarget with
        member _.Exec(pguidCmdGroup: byref<Guid>, nCmdID: uint32, nCmdexecopt: uint32, pvaIn: IntPtr, pvaOut: IntPtr) =
            if isGoToDeclaration pguidCmdGroup nCmdID then
                navigateToDeclaration ()
                VSConstants.S_OK
            else
                match nextTarget with
                | null -> VSConstants.E_FAIL
                | next -> next.Exec(&pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)

        member _.QueryStatus(pguidCmdGroup: byref<Guid>, cCmds: uint32, prgCmds: OLECMD[], pCmdText: IntPtr) =
            if cCmds = 1u && isGoToDeclaration pguidCmdGroup prgCmds[0].cmdID then
                prgCmds[0].cmdf <- uint32 (OLECMDF.OLECMDF_SUPPORTED ||| OLECMDF.OLECMDF_ENABLED)
                VSConstants.S_OK
            else
                match nextTarget with
                | null -> VSConstants.E_FAIL
                | next -> next.QueryStatus(&pguidCmdGroup, cCmds, prgCmds, pCmdText)

[<Export(typeof<IWpfTextViewCreationListener>)>]
[<ContentType(FSharpConstants.FSharpContentTypeName)>]
[<TextViewRole(PredefinedTextViewRoles.PrimaryDocument)>]
type internal GoToDeclarationCommandFilterProvider
    [<ImportingConstructor>]
    (metadataAsSource: FSharpMetadataAsSourceService, editorFactory: IVsEditorAdaptersFactoryService) =

    interface IWpfTextViewCreationListener with
        member _.TextViewCreated(textView) =
            match editorFactory.GetViewAdapter textView with
            | null -> ()
            | viewAdapter -> GoToDeclarationCommandFilter(textView, GoToDefinition(metadataAsSource)).AttachToViewAdapter viewAdapter
