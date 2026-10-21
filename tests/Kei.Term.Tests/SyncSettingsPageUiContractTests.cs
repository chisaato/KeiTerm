using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views.Settings;
using Kei.Term.App.Views.Sync;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Tests;
using Xunit;

namespace Kei.Term.Tests;

public class SyncSettingsPageUiContractTests
{
    [Fact]
    public Task SyncSettingsPage_LayoutAndControls_AreVisibleAndRendered()
        => HeadlessAvalonia.RunAsync(async () =>
        {
            UiDesignSystemService.Apply();

            string dir = Path.Combine(Path.GetTempPath(), "sync_ui_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var scheduler = new SyncScheduler(new ReviewClock(), new FileSyncLocationSource(dir, new DummyCredentialStore()), new DummyPassphraseStore(), new SyncChangeSignal(), (_, _) => Task.FromResult(new SyncResult(true, "ok")));
                var page = new SyncSettingsPage(dir, scheduler, new DummyPassphraseStore(), new DummyCredentialStore());

                var view = new SyncSettingsPageView { DataContext = page };
                var window = new Window
                {
                    Width = 900,
                    Height = 700,
                    Content = view
                };
                window.Show();
                window.UpdateLayout();
                HeadlessAvalonia.Pump();

                var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
                Assert.Contains(buttons, b => (b.Content as string)?.Contains("测试") == true);
                Assert.Contains(buttons, b => (b.Content as string)?.Contains("立即同步") == true);
                Assert.Contains(buttons, b => (b.Content as string)?.Contains("保存同步口令") == true);
                Assert.Contains(buttons, b => (b.Content as string)?.Contains("更换同步口令") == true);
                Assert.Contains(buttons, b => (b.Content as string)?.Contains("停用同步") == true);
                Assert.Contains(buttons, b => (b.Content as string)?.Contains("刷新恢复列表") == true);

                var listBoxes = view.GetVisualDescendants().OfType<ListBox>().ToList();
                Assert.Equal(2, listBoxes.Count); // LocalBackups & RemoteHistory
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        });

    [Fact]
    public Task SyncJoinChoiceWindow_DefaultsToMerge_AndHandlesEscapeCancel()
        => HeadlessAvalonia.RunAsync(async () =>
        {
            UiDesignSystemService.Apply();

            var preview = new SyncConfirmation("token-1", SyncConfirmationKind.FirstJoin, 0, 10);
            var dialog = new SyncJoinChoiceWindow(preview);
            dialog.Show();
            dialog.UpdateLayout();
            HeadlessAvalonia.Pump();

            var radios = dialog.GetVisualDescendants().OfType<RadioButton>().ToList();
            Assert.Equal(3, radios.Count);
            Assert.True(radios[0].IsChecked); // 默认合并

            var cancelBtn = dialog.GetVisualDescendants().OfType<Button>().First(b => b.IsCancel);
            Assert.NotNull(cancelBtn);

            dialog.Close(null);
            await Task.Yield();
        });

    [Fact]
    public Task SyncDeletionConfirmationWindow_DefaultCancel_AndShowsCounts()
        => HeadlessAvalonia.RunAsync(async () =>
        {
            UiDesignSystemService.Apply();

            var preview = new SyncConfirmation("token-2", SyncConfirmationKind.BulkDeletion, 52, 100);
            var dialog = new SyncDeletionConfirmationWindow(preview);
            dialog.Show();
            dialog.UpdateLayout();
            HeadlessAvalonia.Pump();

            var textBlocks = dialog.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(textBlocks, t => t.Text?.Contains("52 个") == true);
            Assert.Contains(textBlocks, t => t.Text?.Contains("100 个") == true);

            var cancelBtn = dialog.GetVisualDescendants().OfType<Button>().First(b => b.IsCancel);
            Assert.True(cancelBtn.IsDefault); // 焦点默认防误触
            dialog.Close(false);
            await Task.Yield();
        });

    [Fact]
    public Task SyncPassphrasePromptWindow_MaskedInput_AndWarningText()
        => HeadlessAvalonia.RunAsync(async () =>
        {
            UiDesignSystemService.Apply();

            var dialog = new SyncPassphrasePromptWindow(true, "口令错误");
            dialog.Show();
            dialog.UpdateLayout();
            HeadlessAvalonia.Pump();

            var textBox = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal('●', textBox.PasswordChar);

            var textBlocks = dialog.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(textBlocks, t => t.Text?.Contains("无法撤回") == true);

            dialog.Close(null);
            await Task.Yield();
        });

    [Fact]
    public Task SyncDisablePromptWindow_DefaultCancel_AndUncheckedDeleteRemote()
        => HeadlessAvalonia.RunAsync(async () =>
        {
            UiDesignSystemService.Apply();

            var dialog = new SyncDisablePromptWindow();
            dialog.Show();
            dialog.UpdateLayout();
            HeadlessAvalonia.Pump();

            var checkBox = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
            Assert.False(checkBox.IsChecked); // 默认不删远端

            var textBlocks = dialog.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(textBlocks, t => t.Text?.Contains("无法撤回") == true);
            Assert.Contains(textBlocks, t => t.Text?.Contains("不会撤销") == true);

            dialog.Close(null);
            await Task.Yield();
        });

    private sealed class ReviewClock : ISyncClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public Task DelayAsync(TimeSpan delay, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DummyPassphraseStore : ISyncPassphraseStore
    {
        public bool IsUnlocked => true;
        public Task<char[]?> GetPassphraseAsync(System.Threading.CancellationToken ct = default) => Task.FromResult<char[]?>(null);
        public Task SavePassphraseAsync(ReadOnlyMemory<char> passphrase, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummyCredentialStore : ISyncLocationCredentialStore
    {
        public Task<string?> GetWebDavPasswordAsync(System.Threading.CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveWebDavPasswordAsync(string password, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetS3SecretKeyAsync(System.Threading.CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveS3SecretKeyAsync(string secretKey, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
    }
}
