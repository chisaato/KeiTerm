using System;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.ViewModels;

// 外观草稿统一由管理器广播，提交、取消、失败补偿都复用同一入口。
public partial class SettingsViewModel
{
    private void BroadcastTerminalFontDraft()
        => _profileManager.NotifyTerminalFontSnapshotChanged(_appearance.DraftFontSnapshot);

    private void OnTerminalProfileDraftPreview(TerminalProfile? value)
    {
        if (_isReloading || IsBusy || value == null || string.IsNullOrWhiteSpace(value.Id)) return;
        _profileManager.NotifyDefaultProfileSelectionChanged(value.Id);
    }

    private void SyncAppearanceToCommitted()
    {
        _isReloading = true;
        try
        {
            _appearance.FontFamily = _lastAppliedSettings.FontFamily;
            _appearance.SetTerminalFallbackFonts(_lastAppliedSettings.TerminalFallbackFonts);
            _appearance.FontWeight = _lastAppliedSettings.TerminalFontWeight;
            _appearance.FontSize = _lastAppliedSettings.FontSize;
            _appearance.CursorBlink = _lastAppliedSettings.CursorBlink;
            _appearance.SetProfiles(_profileManager.AllGuiProfiles, _profileManager.ActiveGuiProfile.Id,
                _profileManager.AllTerminalProfiles, _profileManager.DefaultTerminalProfile.Id);
        }
        finally
        {
            _isReloading = false;
        }
        // 不广播逐字段恢复过程，只发完整的已提交快照，覆盖尚在队列中的草稿。
        BroadcastTerminalFontDraft();
    }
}
