using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

public class TerminalFileDropTests
{
    [Fact]
    public Task ActualTerminalDropRoute_AcceptsLocalFilesAndPastesIntoSession() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = CreateDirectory();
        TerminalTabViewModel tab = new("drag", "monospace", 14);
        Window? window = null;
        try
        {
            string path = Path.Combine(directory, "drag.txt");
            await File.WriteAllTextAsync(path, "data");
            JsonSettingsService settings = new(Path.Combine(directory, "settings.json"));
            settings.Current.FileTransfer.TerminalDropAction = TerminalFileDropAction.PasteFileName;
            tab.ConfigureTerminalFileDrop(settings, new ScriptedInteraction());
            RecordingSession session = new();
            tab.AttachSession(session);
            tab.MarkConnected();
            TerminalConnectionView view = new() { DataContext = tab };
            window = new() { Content = view, Width = 900, Height = 600 };
            window.Show();
            HeadlessAvalonia.Pump();
            ScrollViewer scroll = view.FindControl<ScrollViewer>("TerminalScroll")!;
            Assert.True(DragDrop.GetAllowDrop(scroll));
            using DataTransfer data = new();
            IStorageFile file = Assert.IsAssignableFrom<IStorageFile>(await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)));
            data.Add(DataTransferItem.CreateFile(file));
            DragEventArgs over = new(DragDrop.DragOverEvent, data, scroll, new Point(20, 20), KeyModifiers.None);
            scroll.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            scroll.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, scroll, new Point(20, 20), KeyModifiers.None));
            HeadlessAvalonia.Pump();
            Assert.Equal("'drag.txt'", Encoding.UTF8.GetString(Assert.Single(session.Input)));
            tab.State = ConnectionState.Disconnected;
            DragEventArgs disconnected = new(DragDrop.DragOverEvent, data, scroll, new Point(20, 20), KeyModifiers.None);
            scroll.RaiseEvent(disconnected);
            Assert.Equal(DragDropEffects.None, disconnected.DragEffects);
        }
        finally { window?.Close(); await tab.DisposeAsync(); Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData(TerminalFileDropAction.PastePath)]
    [InlineData(TerminalFileDropAction.PasteFileName)]
    public Task Paste_UsesChosenTextWithoutSubmitting_AndConnectionMemoryEndsOnReconnect(TerminalFileDropAction action)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = CreateDirectory();
        TerminalTabViewModel tab = new("drop", "monospace", 14);
        try
        {
            string path = Path.Combine(directory, "a b'c.txt");
            await File.WriteAllTextAsync(path, "content");
            JsonSettingsService settings = new(Path.Combine(directory, "settings.json"));
            ScriptedInteraction interaction = new();
            interaction.TerminalDropChoices.Enqueue(new(action, TerminalFileDropRememberScope.Connection));
            tab.ConfigureTerminalFileDrop(settings, interaction);
            RecordingSession session = new();
            tab.AttachSession(session);
            tab.MarkConnected();
            await tab.HandleTerminalFileDropAsync([path]);
            await tab.HandleTerminalFileDropAsync([path]);
            Assert.Equal(1, interaction.TerminalDropPrompts);
            Assert.Equal(2, session.Input.Count);
            string expected = action == TerminalFileDropAction.PastePath ? path : Path.GetFileName(path);
            Assert.Equal("'" + expected.Replace("'", "'\"'\"'") + "'", Encoding.UTF8.GetString(session.Input[0]));
            Assert.All(session.Input, input => Assert.DoesNotContain((byte)'\r', input));
            Assert.Null(settings.Current.FileTransfer.TerminalDropAction);

            await tab.DetachSessionAsync();
            RecordingSession replacement = new();
            tab.AttachSession(replacement);
            tab.MarkConnected();
            interaction.TerminalDropChoices.Enqueue(null);
            await tab.HandleTerminalFileDropAsync([path]);
            Assert.Equal(2, interaction.TerminalDropPrompts);
            Assert.Empty(replacement.Input);
        }
        finally { await tab.DisposeAsync(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task GlobalChoice_PersistsAcrossConnections_AndSettingsCanRestoreAsking() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = CreateDirectory();
        TerminalTabViewModel first = new("first", "monospace", 14);
        TerminalTabViewModel second = new("second", "monospace", 14);
        try
        {
            string path = Path.Combine(directory, "file.txt");
            await File.WriteAllTextAsync(path, "payload");
            string settingsPath = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(settingsPath);
            ScriptedInteraction interaction = new();
            interaction.TerminalDropChoices.Enqueue(new(TerminalFileDropAction.PasteFileName, TerminalFileDropRememberScope.Global));
            first.ConfigureTerminalFileDrop(service, interaction);
            first.AttachSession(new RecordingSession());
            first.MarkConnected();
            await first.HandleTerminalFileDropAsync([path]);

            JsonSettingsService reopened = new(settingsPath);
            await reopened.LoadSettingsAsync();
            second.ConfigureTerminalFileDrop(reopened, interaction);
            RecordingSession session = new();
            second.AttachSession(session);
            second.MarkConnected();
            await second.HandleTerminalFileDropAsync([path]);
            Assert.Equal(1, interaction.TerminalDropPrompts);
            Assert.Equal("'file.txt'", Encoding.UTF8.GetString(Assert.Single(session.Input)));

            SettingsViewModel editor = new(reopened, directory);
            FileTransferSettingsPage page = editor.Categories.Select(category => category.Page).OfType<FileTransferSettingsPage>().Single();
            page.SelectedTerminalDropAction = page.TerminalDropActions.Single(option => option.Action == null);
            Assert.True(await editor.ApplyChangesAsync());
            await second.HandleTerminalFileDropAsync([path]);
            Assert.Equal(2, interaction.TerminalDropPrompts);
            Assert.Single(session.Input);
            JsonSettingsService reader = new(settingsPath);
            await reader.LoadSettingsAsync();
            Assert.Null(reader.Current.FileTransfer.TerminalDropAction);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task DisconnectDuringPrompt_CancelsDrop_AndCannotPasteIntoReplacement() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = CreateDirectory();
        TerminalTabViewModel tab = new("disconnect", "monospace", 14);
        try
        {
            string path = Path.Combine(directory, "file.txt");
            await File.WriteAllTextAsync(path, "payload");
            ScriptedInteraction interaction = new() { PendingTerminalDrop = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            tab.ConfigureTerminalFileDrop(new JsonSettingsService(Path.Combine(directory, "settings.json")), interaction);
            RecordingSession previous = new();
            tab.AttachSession(previous);
            tab.MarkConnected();
            Task dropping = tab.HandleTerminalFileDropAsync([path]);
            Assert.True(tab.IsHandlingTerminalDrop);
            await tab.DetachSessionAsync();
            await dropping;
            Assert.False(tab.IsHandlingTerminalDrop);
            RecordingSession replacement = new();
            tab.AttachSession(replacement);
            tab.MarkConnected();
            interaction.PendingTerminalDrop.SetResult(new(TerminalFileDropAction.PastePath, TerminalFileDropRememberScope.Global));
            HeadlessAvalonia.Pump();
            Assert.Empty(previous.Input);
            Assert.Empty(replacement.Input);
        }
        finally { await tab.DisposeAsync(); Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData(TerminalUploadProtocol.Sftp)]
    [InlineData(TerminalUploadProtocol.Scp)]
    [InlineData(TerminalUploadProtocol.ZModem)]
    [InlineData(TerminalUploadProtocol.YModem)]
    public Task ActualSettingsControls_SaveUploadChoice_AndSessionOverridesRemainIndependent(TerminalUploadProtocol protocol)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = CreateDirectory();
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            SettingsViewModel model = new(service, directory);
            window = new(model);
            window.Show();
            model.SelectedCategory = model.Categories.Single(category => category.Page is FileTransferSettingsPage);
            window.UpdateLayout();
            FileTransferSettingsPage page = (FileTransferSettingsPage)model.SelectedCategory.Page;
            ComboBox selector = window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "TerminalUploadProtocolComboBox");
            selector.SelectedItem = page.TerminalUploadProtocols.Single(option => option.Protocol == protocol);
            Assert.True(await model.ApplyChangesAsync());
            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();

            SessionNode session = new() { Host = "server", FileTransferProtocol = FileTransferProtocol.Scp, SftpMode = SftpChannelMode.Dedicated };
            SessionEditViewModel edit = new(session, null, [], reader.Current);
            Assert.Null(edit.SelectedTerminalUpload?.Value);
            TerminalUploadProtocol alternate = protocol == TerminalUploadProtocol.Sftp ? TerminalUploadProtocol.YModem : TerminalUploadProtocol.Sftp;
            edit.SelectedTerminalUpload = edit.TerminalUploadOptions.Single(option => option.Value == alternate);
            edit.ApplyToModel(session);
            ResolvedSessionConfig resolved = SessionConfigBuilder.Build(session, reader.Current);
            Assert.Equal(FileTransferProtocol.Scp, resolved.FileTransferProtocol);
            Assert.Equal(SftpChannelMode.Dedicated, resolved.SftpMode);
            Assert.Equal(alternate, TerminalFileDropPolicy.ResolveProtocol(resolved.TerminalUploadProtocolOverride, reader.Current.FileTransfer.TerminalUploadProtocol));
            Assert.Equal(protocol, reader.Current.FileTransfer.TerminalUploadProtocol);
            edit.SelectedTerminalUpload = edit.TerminalUploadOptions.Single(option => option.Value == null);
            edit.ApplyToModel(session);
            Assert.Equal(protocol, TerminalFileDropPolicy.ResolveProtocol(SessionConfigBuilder.Build(session, reader.Current).TerminalUploadProtocolOverride, reader.Current.FileTransfer.TerminalUploadProtocol));
        }
        finally { window?.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task DisconnectDuringUpload_WaitsForCleanupBeforeDisposingSession() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = CreateDirectory();
        TerminalTabViewModel tab = new("upload", "monospace", 14);
        ControlledUploader uploader = new();
        try
        {
            string path = Path.Combine(directory, "file.txt");
            await File.WriteAllTextAsync(path, "content");
            JsonSettingsService settings = new(Path.Combine(directory, "settings.json"));
            settings.Current.FileTransfer.TerminalDropAction = TerminalFileDropAction.Upload;
            tab.ConfigureTerminalFileDrop(settings, new ScriptedInteraction(), uploader);
            RecordingSession session = new();
            tab.AttachSession(session);
            tab.MarkConnected();
            Task dropping = tab.HandleTerminalFileDropAsync([path]);
            await uploader.Started.Task;
            Assert.True(tab.IsTerminalUploadActive);
            Task detaching = tab.DetachSessionAsync();
            await uploader.Cancelling.Task;
            Assert.False(session.IsDisposed);
            Assert.False(detaching.IsCompleted);
            uploader.FinishCleanup.TrySetResult();
            await Task.WhenAll(dropping, detaching);
            Assert.True(session.IsDisposed);
            Assert.False(tab.IsTerminalUploadActive);
            Assert.False(tab.IsHandlingTerminalDrop);
        }
        finally { uploader.FinishCleanup.TrySetResult(); await tab.DisposeAsync(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task Dialog_ConfirmsSelectedActionAndScope_AndEscapeCancels() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        Window owner = new();
        owner.Show();
        try
        {
            TerminalFileDropPromptViewModel model = new(["/tmp/file.txt"], TerminalUploadProtocol.Sftp, "/remote");
            TerminalFileDropWindow dialog = new(model);
            Task<TerminalFileDropChoice?> result = dialog.ShowDialog<TerminalFileDropChoice?>(owner);
            HeadlessAvalonia.Pump();
            dialog.FindControl<RadioButton>("PasteFileNameAction")!.IsChecked = true;
            dialog.FindControl<ComboBox>("RememberScope")!.SelectedItem = model.RememberScopes.Single(option => option.Scope == TerminalFileDropRememberScope.Connection);
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(new(TerminalFileDropAction.PasteFileName, TerminalFileDropRememberScope.Connection), await result);
            TerminalFileDropWindow cancelled = new(new(["/tmp/file.txt"], TerminalUploadProtocol.Scp, "/remote"));
            Task<TerminalFileDropChoice?> cancellation = cancelled.ShowDialog<TerminalFileDropChoice?>(owner);
            cancelled.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Null(await cancellation);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void PasteRejectsTerminalControlCharacters()
        => Assert.Throws<ArgumentException>(() => TerminalFileDropPolicy.BuildPasteText(["/tmp/file\ncommand"], TerminalFileDropAction.PastePath));

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingSession : ITerminalSession
    {
        public List<byte[]> Input { get; } = [];
        public bool IsDisposed { get; private set; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) { Input.Add(data.ToArray()); return Task.CompletedTask; }
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { IsDisposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlledUploader : ITerminalFileUploader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task UploadAsync(ITerminalSession session, IReadOnlyList<string> paths, TerminalUploadProtocol protocol,
            string remoteDirectory, IProgress<TerminalUploadProgress>? progress = null, CancellationToken ct = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                Cancelling.TrySetResult();
                await FinishCleanup.Task;
            }
        }
    }

}
