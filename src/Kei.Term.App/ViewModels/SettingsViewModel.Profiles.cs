using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

// 配色方案编辑、导入与导出，独立于通用设置的加载和保存。
public partial class SettingsViewModel
{
    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        if (IsBusy || SaveBundleFileDialogAsync == null) return;
        var filePath = await SaveBundleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            // 导出“已提交”权威，不含未应用草稿
            var json = _profileManager.ExportBundle();
            await System.IO.File.WriteAllTextAsync(filePath, json);
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    string.Format(Strings.Get("Settings.Appearance.BundleExportSuccess"), System.IO.Path.GetFileName(filePath)));
            }
        }
        catch (Exception ex)
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        if (IsBusy || OpenBundleFileDialogAsync == null) return;
        var filePath = await OpenBundleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            // 纳入父事务：只并入有效状态，Apply 时统一提交；取消无副作用、不落盘
            await _profileManager.ImportBundleAsync(json, overwrite: true, persist: false);
            RefreshAllProfiles();
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    Strings.Get("Settings.Appearance.BundleImportSuccess"));
            }
        }
        catch (Exception ex)
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("Settings.Appearance.ProfileBackupSection"),
                    string.Format(Strings.Get("Settings.Appearance.BundleImportError"), ex.Message));
            }
        }
    }

    [RelayCommand]
    private async Task NewTerminalProfileAsync()
    {
        if (IsBusy || OpenTerminalProfileEditDialogAsync == null) return;

        var draft = new TerminalProfile { Name = "新建终端主题" };
        var confirmed = await OpenTerminalProfileEditDialogAsync(draft, _appearance.DraftFontSnapshot);
        if (confirmed == null) return;

        // 子编辑器确认：仅并入父设置草稿并广播，不提前持久化
        _profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
        RefreshTerminalProfiles(confirmed.Id);
    }

    [RelayCommand]
    private async Task EditTerminalProfileAsync()
    {
        if (IsBusy || OpenTerminalProfileEditDialogAsync == null || _appearance.SelectedTerminalProfile == null) return;

        var confirmed = await OpenTerminalProfileEditDialogAsync(
            _appearance.SelectedTerminalProfile,
            _appearance.DraftFontSnapshot);
        if (confirmed == null) return;

        // 同 ID 内容修改：并入草稿并广播内容变更（ID 不变也刷新），不提前持久化
        _profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
        RefreshTerminalProfiles(confirmed.Id);
    }

    [RelayCommand]
    private async Task ImportKonsoleSchemeAsync()
    {
        if (IsBusy || OpenKonsoleFileDialogAsync == null || OpenTerminalProfileEditDialogAsync == null) return;
        var filePath = await OpenKonsoleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;

        try
        {
            var content = await System.IO.File.ReadAllTextAsync(filePath);
            var defaultName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var parsed = Kei.Term.Core.Services.KonsoleColorSchemeParser.Parse(content, defaultName);

            // 使用当前字体草稿快照；配色导入不读取也不写回字体设置
            var confirmed = await OpenTerminalProfileEditDialogAsync(parsed, _appearance.DraftFontSnapshot);
            if (confirmed == null) return; // 取消无副作用

            // 确认后才并入父草稿并广播；落盘延后到“应用”
            _profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
            RefreshTerminalProfiles(confirmed.Id);
        }
        catch (Exception ex)
        {
            await NotifyAsync(Strings.Get("TerminalProfileEdit.Title"), ex.Message);
        }
    }

    private void RefreshTerminalProfiles(string selectedId)
    {
        _appearance.TerminalProfiles.Clear();
        foreach (var p in _profileManager.AllTerminalProfiles)
        {
            _appearance.TerminalProfiles.Add(p);
        }
        _appearance.SelectedTerminalProfile = _appearance.TerminalProfiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _appearance.TerminalProfiles.FirstOrDefault();
    }

    // Bundle 导入后刷新两套 Profile 下拉（抑制预览广播）
    private void RefreshAllProfiles()
    {
        _isReloading = true;
        try
        {
            _appearance.SetProfiles(
                _profileManager.AllGuiProfiles,
                _profileManager.ActiveGuiProfile.Id,
                _profileManager.AllTerminalProfiles,
                _profileManager.DefaultTerminalProfile.Id);
        }
        finally
        {
            _isReloading = false;
        }
    }
}
