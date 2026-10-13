using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.ViewModels;

public enum ConnectionDocumentKind
{
    Shell,
    FileManager
}

// 连接标签内的文档状态；关闭一块内容不会释放另一块仍在使用的连接。
public partial class TerminalTabViewModel
{
    [ObservableProperty]
    private bool _isShellVisible = true;

    [ObservableProperty]
    private ConnectionDocumentKind _activeDocument = ConnectionDocumentKind.Shell;

    public bool HasOpenDocuments => IsShellVisible || IsFileManagerVisible;

    partial void OnIsShellVisibleChanged(bool value)
    {
        if (!value && IsFileManagerVisible) ActiveDocument = ConnectionDocumentKind.FileManager;
        WeakReferenceMessenger.Default.Send(new ConnectionDocumentsChangedMessage(this));
    }

    partial void OnIsFileManagerVisibleChanged(bool value)
    {
        if (!value && IsShellVisible) ActiveDocument = ConnectionDocumentKind.Shell;
    }

    // 返回是否还有文档；快捷键调用者仅在全部关闭后释放外层标签。
    public bool CloseActiveDocument()
    {
        ConnectionDocumentKind target = ActiveDocument;
        if (target == ConnectionDocumentKind.FileManager && !IsFileManagerVisible)
            target = ConnectionDocumentKind.Shell;
        else if (target == ConnectionDocumentKind.Shell && !IsShellVisible)
            target = ConnectionDocumentKind.FileManager;
        CloseDocument(target);
        return HasOpenDocuments;
    }

    [RelayCommand]
    private void CloseShellDocument()
    {
        CloseDocument(ConnectionDocumentKind.Shell);
        RequestCloseEmptyTab();
    }

    public void CloseFileManagerDocument()
    {
        CloseDocument(ConnectionDocumentKind.FileManager);
        RequestCloseEmptyTab();
    }

    private void CloseDocument(ConnectionDocumentKind document)
    {
        if (document == ConnectionDocumentKind.FileManager) IsFileManagerVisible = false;
        else IsShellVisible = false;
    }

    private void RequestCloseEmptyTab()
    {
        if (!HasOpenDocuments)
            WeakReferenceMessenger.Default.Send(new ConnectionDocumentsChangedMessage(this, CloseEmptyTab: true));
    }
}
