using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.Logging;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.Views;

public partial class IdentityManagerWindow : Window
{
    public IdentityManagerWindow()
    {
        InitializeComponent();
    }

    public IdentityManagerWindow(IdentityManagerViewModel vm, ILogger? logger = null) : this()
    {
        DataContext = vm;
        // 优先使用窗口层注入的 logger，否则复用管理器 VM 的 logger
        var log = logger ?? vm.Logger;

        // 编辑器以本管理器窗口为 owner 模态打开；「应用」时由编辑器自身完成落库
        vm.OpenEditDialogAsync = existing => Safe.RunAsync<IdentityEditResult?>(log, "打开身份编辑器", async () =>
        {
            log.LogInformation("身份编辑器打开请求 模式={Mode} 身份Id={IdentityId}", existing == null ? "新建" : "编辑", existing?.Id);
            var editVm = new IdentityEditViewModel(existing, log);

            // Vault 私钥导入口令输入（一次性）
            editVm.VaultKeyPassphrasePrompt = keyPath =>
            {
                var promptWindow = PassphrasePromptWindow.ForVaultImport(keyPath);
                return promptWindow.ShowDialog<PassphrasePromptResult?>(this);
            };
            editVm.VaultKeyInfoLoader = vm.VaultKeyInfoLoader;
            editVm.SaveIdentityAsync = vm.SaveIdentityAsync;
            editVm.PersistVaultKeysAsync = vm.PersistVaultKeysAsync;

            // 编辑已有身份时回显已存 Vault 私钥材料（字节数 + 指纹）
            await editVm.InitializeVaultKeyStatusAsync();

            var win = new IdentityEditWindow(editVm);
            await win.ShowDialog(this);

            // 从未「应用」= 取消返回 null；已应用（含点取消/标题栏关闭）返回已保存身份用于刷新列表
            log.LogInformation("身份编辑器关闭 结果={Result}", editVm.AppliedIdentity == null ? "取消" : "已应用");
            return editVm.AppliedIdentity == null
                ? null
                : new IdentityEditResult(editVm.AppliedIdentity, []);
        });

        // 明文横幅「设置主密码」：以本窗口为 owner 弹初始化框（异常按取消语义返回）
        vm.VaultSetupDialogAsync = () =>
        {
            try
            {
                var win = new VaultSetupWindow();
                return win.ShowDialog<VaultSetupResult>(this);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "打开保管库初始化窗口 失败");
                return Task.FromResult(new VaultSetupResult(VaultSetupChoice.Cancel, null));
            }
        };

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
