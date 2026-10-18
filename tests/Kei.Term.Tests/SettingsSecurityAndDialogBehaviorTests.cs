using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Infrastructure.Vault;

namespace Kei.Term.Tests;

public class SettingsSecurityAndDialogBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemoveMasterPassword_RealConfirmationCancelOrConfirmChangesVault(bool confirm)
        => WithVault(async vault =>
        {
            await vault.SetMasterPasswordAsync("original-password");
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync(vault);
            SelectSecurity(context);
            SecuritySettingsPage page = context.Model.Security;
            Assert.True(page.RemoveMasterPasswordCommand.CanExecute(null));
            Button remove = CommandButton(context.Window, page.RemoveMasterPasswordCommand);
            SettingsHeadlessTestContext.Click(context.Window, remove);
            Window dialog = context.Dialog<Window>();
            Assert.Same(context.Window, dialog.Owner);
            Button answer = dialog.GetVisualDescendants().OfType<Button>().Single(b => confirm ? b.IsDefault : b.IsCancel);
            SettingsHeadlessTestContext.Click(dialog, answer);
            await page.RemoveMasterPasswordCommand.ExecutionTask!;
            Assert.Equal(confirm, vault.IsPlainMode);
            Assert.Equal(!confirm, page.CanRemoveMasterPassword);
            Assert.Equal(!confirm, page.RemoveMasterPasswordCommand.CanExecute(null));
            Assert.Empty(context.Window.OwnedWindows);
            // 主密码修改立即生效，不被整个设置窗口的“取消”回滚。
            await context.Model.CancelCommand.ExecuteAsync(null);
            Assert.Equal(confirm, vault.IsPlainMode);
            if (!confirm)
            {
                vault.Lock();
                await vault.UnlockAsync("original-password", false);
                Assert.True(vault.IsUnlocked);
            }
        });

    [Fact]
    public Task SetAndChangeMasterPassword_ValidatesInputAndCancelPreservesExistingPassword()
        => WithVault(async vault =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync(vault);
            SelectSecurity(context);
            SecuritySettingsPage page = context.Model.Security;
            Assert.False(page.CanRemoveMasterPassword);
            string initialStatus = page.MasterPasswordStatus;
            SettingsHeadlessTestContext.Click(context.Window, CommandButton(context.Window, page.SetMasterPasswordCommand));
            VaultSetupWindow setup = context.Dialog<VaultSetupWindow>();
            Button confirm = setup.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("accent"));
            SettingsHeadlessTestContext.Click(setup, confirm);
            Assert.True(setup.FindControl<TextBlock>("ErrorText")!.IsVisible);
            Assert.True(vault.IsPlainMode);
            setup.FindControl<TextBox>("PasswordBox")!.Text = "new-password";
            setup.FindControl<TextBox>("ConfirmBox")!.Text = "mismatch";
            SettingsHeadlessTestContext.Click(setup, confirm);
            Assert.True(setup.IsVisible);
            Assert.True(vault.IsPlainMode);
            setup.FindControl<TextBox>("ConfirmBox")!.Text = "new-password";
            SettingsHeadlessTestContext.Click(setup, confirm);
            await page.SetMasterPasswordCommand.ExecutionTask!;
            Assert.False(vault.IsPlainMode);
            Assert.True(page.CanRemoveMasterPassword);
            Assert.NotEqual(initialStatus, page.MasterPasswordStatus);

            SettingsHeadlessTestContext.Click(context.Window, CommandButton(context.Window, page.SetMasterPasswordCommand));
            VaultSetupWindow change = context.Dialog<VaultSetupWindow>();
            Assert.False(change.FindControl<RadioButton>("PlainRadio")!.IsVisible);
            change.FindControl<TextBox>("PasswordBox")!.Text = "discarded-password";
            change.FindControl<TextBox>("ConfirmBox")!.Text = "discarded-password";
            Button cancel = change.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.Get("Common.Cancel")));
            SettingsHeadlessTestContext.Click(change, cancel);
            await page.SetMasterPasswordCommand.ExecutionTask!;
            vault.Lock();
            await vault.UnlockAsync("new-password", false);
            Assert.True(vault.IsUnlocked);

            SettingsHeadlessTestContext.Click(context.Window, CommandButton(context.Window, page.SetMasterPasswordCommand));
            change = context.Dialog<VaultSetupWindow>();
            change.FindControl<TextBox>("PasswordBox")!.Text = "changed-password";
            change.FindControl<TextBox>("ConfirmBox")!.Text = "changed-password";
            SettingsHeadlessTestContext.Click(change, change.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("accent")));
            await page.SetMasterPasswordCommand.ExecutionTask!;
            vault.Lock();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.UnlockAsync("new-password", false));
            await vault.UnlockAsync("changed-password", false);
            Assert.True(vault.IsUnlocked);
        });

    [Fact]
    public Task LockedVault_RejectsChangeAndRemovalUntilUnlocked()
        => WithVault(async vault =>
        {
            await vault.SetMasterPasswordAsync("locked-password");
            vault.Lock();
            SecuritySettingsPage page = new(vault);
            ScriptedInteraction interaction = new();
            page.Interaction = () => interaction;
            bool setupRequested = false;
            page.VaultSetupDialogAsync = _ =>
            {
                setupRequested = true;
                return Task.FromResult<VaultSetupResult?>(new(VaultSetupChoice.SetMasterPassword, "unexpected"));
            };
            await page.SetMasterPasswordCommand.ExecuteAsync(null);
            await page.RemoveMasterPasswordCommand.ExecuteAsync(null);
            Assert.False(setupRequested);
            Assert.Equal(2, interaction.Notifications.Count);
            Assert.All(interaction.Notifications, notification => Assert.Equal(Strings.Get("Settings.Security.Locked"), notification.Message));
            Assert.False(vault.IsPlainMode);
            Assert.False(vault.IsUnlocked);
            await vault.UnlockAsync("locked-password", false);
            await page.RemoveMasterPasswordCommand.ExecuteAsync(null);
            Assert.True(vault.IsPlainMode);
            Assert.False(page.CanRemoveMasterPassword);
        });

    [Theory]
    [InlineData("closed")]
    [InlineData("plain")]
    [InlineData("empty")]
    public Task SetupWithoutNewPassword_DoesNotReplaceExistingVault(string response)
        => WithVault(async vault =>
        {
            await vault.SetMasterPasswordAsync("preserved-password");
            SecuritySettingsPage page = new(vault);
            bool? changingPassword = null;
            page.VaultSetupDialogAsync = changing =>
            {
                changingPassword = changing;
                VaultSetupResult? result = response switch
                {
                    "closed" => null,
                    "plain" => new(VaultSetupChoice.PlainMode, null),
                    _ => new(VaultSetupChoice.SetMasterPassword, string.Empty),
                };
                return Task.FromResult(result);
            };
            await page.SetMasterPasswordCommand.ExecuteAsync(null);
            Assert.True(changingPassword);
            Assert.False(vault.IsPlainMode);
            Assert.True(page.CanRemoveMasterPassword);
            vault.Lock();
            await vault.UnlockAsync("preserved-password", false);
            Assert.True(vault.IsUnlocked);
        });

    [Fact]
    public Task MissingDialogOrInteraction_DoesNotChangeProtectedVault()
        => WithVault(async vault =>
        {
            await vault.SetMasterPasswordAsync("preserved-password");
            SecuritySettingsPage page = new(vault);
            await page.SetMasterPasswordCommand.ExecuteAsync(null);
            await page.RemoveMasterPasswordCommand.ExecuteAsync(null);
            Assert.False(vault.IsPlainMode);
            vault.Lock();
            await vault.UnlockAsync("preserved-password", false);
            Assert.True(vault.IsUnlocked);

            // 无保险库时，即便接线了设置弹窗，也不能启动一次没有目标的主密码编辑。
            SecuritySettingsPage unavailable = new();
            bool opened = false;
            unavailable.VaultSetupDialogAsync = _ =>
            {
                opened = true;
                return Task.FromResult<VaultSetupResult?>(new(VaultSetupChoice.SetMasterPassword, "unused"));
            };
            await unavailable.SetMasterPasswordCommand.ExecuteAsync(null);
            Assert.False(opened);
            Assert.False(unavailable.RemoveMasterPasswordCommand.CanExecute(null));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProxyAddDialog_ValidationAndSaveCancelAffectRealRepository(bool save)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
            ProxySettingsPage page = context.Model.ProxyPage!;
            context.Model.SelectedCategory = context.Model.Categories.Single(c => ReferenceEquals(c.Page, page));
            context.Layout();
            Button add = context.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddProxyButton");
            SettingsHeadlessTestContext.Click(context.Window, add);
            ProxyEditWindow dialog = context.Dialog<ProxyEditWindow>();
            ProxyEditViewModel draft = Assert.IsType<ProxyEditViewModel>(dialog.DataContext);
            Button saveButton = dialog.FindControl<Button>("SaveButton")!;
            TextBox hostInput = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "127.0.0.1");
            hostInput.Text = string.Empty;
            SettingsHeadlessTestContext.Click(dialog, saveButton);
            Assert.True(draft.HasError);
            Assert.Empty(await context.Proxies.GetAllAsync());
            TextBox[] inputs = dialog.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.FindAncestorOfType<NumericUpDown>() == null).ToArray();
            SettingsHeadlessTestContext.AssertVisible(inputs[0]);
            inputs[0].Text = "headless-proxy";
            inputs[1].Text = "127.0.0.1";
            Assert.Equal("headless-proxy", draft.Name);
            Assert.Equal("127.0.0.1", draft.Host);
            SettingsHeadlessTestContext.Click(dialog, save ? saveButton : dialog.FindControl<Button>("CancelButton")!);
            await page.AddCommand.ExecutionTask!;
            Assert.Equal(save ? 1 : 0, (await context.Proxies.GetAllAsync()).Count);
            Assert.Equal(save ? 1 : 0, page.Rows.Count);
            if (save)
            {
                Assert.Equal("headless-proxy", Assert.Single(page.Rows).Name);
                ListBox list = context.Window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProxyList");
                SettingsHeadlessTestContext.AssertVisible(list);
                list.SelectedItem = null;
                Assert.Null(page.SelectedRow);
                Assert.False(page.EditCommand.CanExecute(null));
                list.SelectedItem = page.Rows[0];
                Assert.Same(page.Rows[0], page.SelectedRow);
                Assert.True(page.EditCommand.CanExecute(null));
                Button edit = context.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "EditProxyButton");
                foreach (bool acceptEdit in new[] { false, true })
                {
                    SettingsHeadlessTestContext.Click(context.Window, edit);
                    ProxyEditWindow editor = context.Dialog<ProxyEditWindow>();
                    TextBox name = editor.GetVisualDescendants().OfType<TextBox>().First();
                    Assert.Equal("headless-proxy", name.Text);
                    name.Text = "edited-proxy";
                    SettingsHeadlessTestContext.Click(editor, editor.FindControl<Button>(acceptEdit ? "SaveButton" : "CancelButton")!);
                    await page.EditCommand.ExecutionTask!;
                    Assert.Equal(acceptEdit ? "edited-proxy" : "headless-proxy", Assert.Single(await context.Proxies.GetAllAsync()).Name);
                    Assert.Equal(acceptEdit ? "edited-proxy" : "headless-proxy", Assert.Single(page.Rows).Name);
                }
                Button delete = context.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DeleteProxyButton");
                SettingsHeadlessTestContext.Click(context.Window, delete);
                await page.DeleteCommand.ExecutionTask!;
                Assert.Empty(await context.Proxies.GetAllAsync());
                Assert.Empty(page.Rows);
            }
        });

    private static void SelectSecurity(SettingsHeadlessTestContext context)
    {
        context.Model.SelectedCategory = context.Model.Categories.Single(c => c.Page is SecuritySettingsPage);
        context.Layout();
    }

    private static Button CommandButton(Window window, System.Windows.Input.ICommand command)
        => window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == command && b.IsEffectivelyVisible);

    private static Task WithVault(Func<InternalVaultManager, Task> test)
        => HeadlessAvalonia.RunAsync(async () =>
        {
            string path = VaultTestDb.NewPath();
            try
            {
                string connection = $"Data Source={path}";
                await VaultTestDb.CreateAsync(connection, Guid.NewGuid());
                InternalVaultManager vault = VaultTestDb.CreateVault(connection);
                try { await test(vault); }
                finally { vault.Lock(); }
            }
            finally { File.Delete(path); }
        });
}
