using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Ssh.Services;

namespace Kei.Term.App.ViewModels;

public partial class TerminalTabViewModel
{
    private ISettingsService? _dropSettings;
    private IInteractionService _dropInteraction = NullInteractionService.Instance;
    private ITerminalFileUploader _terminalUploader = new TerminalFileUploader();
    private TerminalFileDropAction? _connectionDropAction;
    private CancellationTokenSource? _dropCancellation;
    private Task? _terminalDropTask;

    [ObservableProperty]
    private bool _isHandlingTerminalDrop;

    [ObservableProperty]
    private bool _isTerminalUploadActive;

    [ObservableProperty]
    private string _terminalUploadStatus = string.Empty;

    public TerminalUploadProtocol EffectiveTerminalUploadProtocol => TerminalFileDropPolicy.ResolveProtocol(
        Config?.TerminalUploadProtocolOverride, _dropSettings?.Current.FileTransfer.TerminalUploadProtocol ?? TerminalUploadProtocol.Scp);

    public void ConfigureTerminalFileDrop(ISettingsService settings, IInteractionService interaction, ITerminalFileUploader? uploader = null)
    {
        _dropSettings = settings;
        _dropInteraction = interaction;
        if (uploader != null) _terminalUploader = uploader;
    }

    public Task HandleTerminalFileDropAsync(IReadOnlyList<string> paths)
    {
        if (IsHandlingTerminalDrop || IsDisposed || State != ConnectionState.Connected || _session is not { IsConnected: true } session)
            return Task.CompletedTask;
        string[] localPaths = paths.Where(path => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path))).Distinct().ToArray();
        if (localPaths.Length == 0) return Task.CompletedTask;

        IsHandlingTerminalDrop = true;
        CancellationTokenSource cancellation = new();
        _dropCancellation = cancellation;
        Task operation = HandleTerminalFileDropCoreAsync(session, localPaths, cancellation);
        _terminalDropTask = operation;
        return operation;
    }

    private async Task HandleTerminalFileDropCoreAsync(ITerminalSession session, string[] paths, CancellationTokenSource cancellation)
    {
        CancellationToken ct = cancellation.Token;
        try
        {
            TerminalUploadProtocol protocol = EffectiveTerminalUploadProtocol;
            // 目录跟随与文件拖放共用同一份 OSC7/tracker 结果（见 DirectoryFollow 分部）；
            // 无有效远端目录时回退到文件管理器当前目录，再退到工作目录。
            string directory = _terminalWorkingDirectory ?? FileManager?.CurrentPath ?? ".";
            TerminalFileDropAction? action = TerminalFileDropPolicy.ResolveAction(
                _connectionDropAction, _dropSettings?.Current.FileTransfer.TerminalDropAction);
            if (action == null)
            {
                TerminalFileDropChoice? choice = await _dropInteraction.PromptTerminalFileDropAsync(paths, protocol, directory, ct).WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (choice == null || IsDisposed || !ReferenceEquals(session, _session) || !session.IsConnected) return;
                action = choice.Action;
                if (choice.RememberScope == TerminalFileDropRememberScope.Connection) _connectionDropAction = action;
                else if (choice.RememberScope == TerminalFileDropRememberScope.Global && _dropSettings != null)
                    await _dropSettings.CommitTerminalDropActionAsync(action, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _session) || !session.IsConnected) return;

            if (action == TerminalFileDropAction.Upload)
            {
                // 目录仍可粘贴路径/名称；上传仅接收普通文件，避免静默丢弃部分拖入项。
                if (paths.Any(Directory.Exists))
                {
                    await _dropInteraction.NotifyAsync(Strings.Get("Terminal.Drop.Title"), Strings.Get("Terminal.Drop.DirectoriesUnsupported"));
                    return;
                }
                IsTerminalUploadActive = true;
                TerminalUploadStatus = Strings.Format("Terminal.Drop.Uploading", TerminalFileDropOptions.Describe(protocol));
                IProgress<TerminalUploadProgress> progress = new Progress<TerminalUploadProgress>(value => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_dropCancellation, cancellation) || !IsTerminalUploadActive) return;
                    double percent = value.TotalBytes > 0 ? Math.Min(100, 100d * value.BytesTransferred / value.TotalBytes) : 0;
                    TerminalUploadStatus = $"{TerminalFileDropOptions.Describe(protocol)} · {Path.GetFileName(value.FilePath)} · {percent:F0}%";
                }));
                await Task.Run(() => _terminalUploader.UploadAsync(session, paths, protocol, directory, progress, ct), ct);
                StatusMessage = Strings.Get("Terminal.Drop.UploadComplete");
                if (FileManager != null) await FileManager.RefreshDirectoryAsync();
            }
            else
            {
                string text = TerminalFileDropPolicy.BuildPasteText(paths, action.Value);
                // 只发送输入，远端 shell/编辑器负责插入当前光标；不追加回车。
                await session.SendInputAsync(Encoding.UTF8.GetBytes(text), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Terminal.Drop.Failed", ex.Message);
            await _dropInteraction.NotifyAsync(Strings.Get("Terminal.Drop.Title"), StatusMessage);
        }
        finally
        {
            if (ReferenceEquals(_dropCancellation, cancellation))
            {
                _dropCancellation = null;
                _terminalDropTask = null;
                IsHandlingTerminalDrop = false;
                IsTerminalUploadActive = false;
            }
            cancellation.Dispose();
            if (!IsDisposed && ReferenceEquals(session, _session)) _terminal?.Focus();
        }
    }

    [RelayCommand]
    private void CancelTerminalUpload() => _dropCancellation?.Cancel();

    private async Task DrainTerminalFileDropAsync()
    {
        _dropCancellation?.Cancel();
        Task? operation = _terminalDropTask;
        if (operation != null) await operation;
    }
}
