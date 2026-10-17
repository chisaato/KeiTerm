namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;

public partial class SessionEditViewModel
{
    private ISessionMetadataRepository? _metadataRepo;

    public IReadOnlyList<InheritableOption<bool>> VerifyHashOptions { get; private set; } = [];
    public IReadOnlyList<InheritableOption<bool>> ArchiveDirectoriesOptions { get; private set; } = [];

    [ObservableProperty]
    private InheritableOption<bool>? _selectedVerifyHash;

    [ObservableProperty]
    private InheritableOption<bool>? _selectedArchiveDirectories;

    [ObservableProperty]
    private bool _hasProbeResult;

    [ObservableProperty]
    private string _probeStatusText = Strings.Get("SessionEdit.ProbeNotRun");

    [ObservableProperty]
    private string _probePlatformText = string.Empty;

    [ObservableProperty]
    private string _probeHashesText = string.Empty;

    [ObservableProperty]
    private string _probeArchivesText = string.Empty;

    [ObservableProperty]
    private string _probeTimeText = string.Empty;

    public bool CanClearProbe => !IsNewSession && _metadataRepo != null;

    private void InitializeTransferCapabilities(
        SessionOverrides overrides,
        AppSettings global,
        ISessionMetadataRepository? metadataRepo)
    {
        _metadataRepo = metadataRepo;

        VerifyHashOptions = BuildBooleanInheritableOptions(
            global.FileTransfer.VerifyHashEnabled,
            "Common.Enabled",
            "Common.Disabled");

        ArchiveDirectoriesOptions = BuildBooleanInheritableOptions(
            global.FileTransfer.ArchiveDirectoriesEnabled,
            "Common.Enabled",
            "Common.Disabled");

        SelectedVerifyHash = VerifyHashOptions.FirstOrDefault(opt => opt.Value == overrides.VerifyHashEnabled)
            ?? VerifyHashOptions[0];

        SelectedArchiveDirectories = ArchiveDirectoriesOptions.FirstOrDefault(opt => opt.Value == overrides.ArchiveDirectoriesEnabled)
            ?? ArchiveDirectoriesOptions[0];

        _ = LoadProbeResultAsync();
    }

    private static IReadOnlyList<InheritableOption<bool>> BuildBooleanInheritableOptions(
        bool globalValue,
        string enabledKey,
        string disabledKey)
    {
        string globalDesc = Strings.Get(globalValue ? enabledKey : disabledKey);
        return
        [
            new InheritableOption<bool>(null, Strings.Format("Behavior.Inherit", globalDesc)),
            new InheritableOption<bool>(true, Strings.Get(enabledKey)),
            new InheritableOption<bool>(false, Strings.Get(disabledKey)),
        ];
    }

    public async Task LoadProbeResultAsync()
    {
        if (IsNewSession || _metadataRepo == null)
        {
            HasProbeResult = false;
            ProbeStatusText = Strings.Get("SessionEdit.ProbeNotRun");
            return;
        }

        TransferProbeResult? result = await _metadataRepo.GetTransferProbeAsync(NodeId);
        DisplayProbeResult(result);
    }

    public void DisplayProbeResult(TransferProbeResult? result)
    {
        if (result == null)
        {
            HasProbeResult = false;
            ProbeStatusText = Strings.Get("SessionEdit.ProbeNotRun");
            ProbePlatformText = string.Empty;
            ProbeHashesText = string.Empty;
            ProbeArchivesText = string.Empty;
            ProbeTimeText = string.Empty;
            return;
        }

        HasProbeResult = true;
        ProbePlatformText = Strings.Format("SessionEdit.ProbePlatform", result.Platform.ToString());

        string hashes = result.Hashes.Count > 0
            ? string.Join(", ", result.Hashes.Select(h => h.ToString()))
            : Strings.Get("Common.None");
        ProbeHashesText = Strings.Format("SessionEdit.ProbeHashes", hashes);

        string archives = result.Archives.Count > 0
            ? string.Join(", ", result.Archives.Select(a => a.ToString()))
            : Strings.Get("Common.None");
        ProbeArchivesText = Strings.Format("SessionEdit.ProbeArchives", archives);

        string time = result.ProbedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        ProbeTimeText = Strings.Format("SessionEdit.ProbeTime", time);
    }

    [RelayCommand]
    public async Task ClearProbeAsync()
    {
        if (!CanClearProbe || _metadataRepo == null) return;
        await _metadataRepo.ClearTransferProbeAsync(NodeId);
        await LoadProbeResultAsync();
    }

    private void ApplyTransferCapabilities(SessionOverrides overrides)
    {
        overrides.VerifyHashEnabled = SelectedVerifyHash?.Value;
        overrides.ArchiveDirectoriesEnabled = SelectedArchiveDirectories?.Value;
    }
}
