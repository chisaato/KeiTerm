namespace Kei.Term.App.ViewModels.Settings;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Transfers;

public partial class TransferSettingsPage
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HashAlgorithmSummary))]
    private bool _verifyHashEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveFormatSummary))]
    private bool _archiveDirectoriesEnabled;

    public ObservableCollection<TransferOrderedItemViewModel<TransferHashAlgorithm>> HashAlgorithms { get; } = [];
    public ObservableCollection<TransferOrderedItemViewModel<TransferArchiveFormat>> ArchiveFormats { get; } = [];

    [ObservableProperty]
    private TransferOrderedItemViewModel<TransferHashAlgorithm>? _selectedHashAlgorithm;

    [ObservableProperty]
    private TransferOrderedItemViewModel<TransferArchiveFormat>? _selectedArchiveFormat;

    public string HashAlgorithmSummary
    {
        get
        {
            var active = HashAlgorithms.Where(h => h.IsEnabled).Select(h => h.DisplayName).ToList();
            return active.Count > 0 ? string.Join(" › ", active) : Strings.Get("Settings.FileTransfer.NoAlgorithmsEnabled");
        }
    }

    public string ArchiveFormatSummary
    {
        get
        {
            var active = ArchiveFormats.Where(a => a.IsEnabled).Select(a => a.DisplayName).ToList();
            return active.Count > 0 ? string.Join(" › ", active) : Strings.Get("Settings.FileTransfer.NoFormatsEnabled");
        }
    }

    public void InitializeTransferPreferences(
        bool verifyHashEnabled,
        IReadOnlyList<TransferHashAlgorithm>? hashOrder,
        bool archiveDirectoriesEnabled,
        IReadOnlyList<TransferArchiveFormat>? archiveOrder)
    {
        VerifyHashEnabled = verifyHashEnabled;
        ArchiveDirectoriesEnabled = archiveDirectoriesEnabled;

        PopulateHashAlgorithms(hashOrder);
        PopulateArchiveFormats(archiveOrder);
    }

    private void PopulateHashAlgorithms(IReadOnlyList<TransferHashAlgorithm>? order)
    {
        HashAlgorithms.Clear();
        List<TransferHashAlgorithm> defined = Enum.GetValues<TransferHashAlgorithm>().ToList();
        HashSet<TransferHashAlgorithm> seen = [];

        if (order != null)
        {
            foreach (TransferHashAlgorithm item in order)
            {
                if (Enum.IsDefined(item) && seen.Add(item))
                {
                    var vm = new TransferOrderedItemViewModel<TransferHashAlgorithm>(item, FormatHashAlgorithm(item), true);
                    vm.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HashAlgorithmSummary));
                    HashAlgorithms.Add(vm);
                }
            }
        }

        foreach (TransferHashAlgorithm item in defined)
        {
            if (seen.Add(item))
            {
                var vm = new TransferOrderedItemViewModel<TransferHashAlgorithm>(item, FormatHashAlgorithm(item), false);
                vm.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HashAlgorithmSummary));
                HashAlgorithms.Add(vm);
            }
        }

        SelectedHashAlgorithm = HashAlgorithms.FirstOrDefault();
        OnPropertyChanged(nameof(HashAlgorithmSummary));
    }

    private void PopulateArchiveFormats(IReadOnlyList<TransferArchiveFormat>? order)
    {
        ArchiveFormats.Clear();
        List<TransferArchiveFormat> defined = Enum.GetValues<TransferArchiveFormat>().ToList();
        HashSet<TransferArchiveFormat> seen = [];

        if (order != null)
        {
            foreach (TransferArchiveFormat item in order)
            {
                if (Enum.IsDefined(item) && seen.Add(item))
                {
                    var vm = new TransferOrderedItemViewModel<TransferArchiveFormat>(item, FormatArchiveFormat(item), true);
                    vm.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ArchiveFormatSummary));
                    ArchiveFormats.Add(vm);
                }
            }
        }

        foreach (TransferArchiveFormat item in defined)
        {
            if (seen.Add(item))
            {
                var vm = new TransferOrderedItemViewModel<TransferArchiveFormat>(item, FormatArchiveFormat(item), false);
                vm.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ArchiveFormatSummary));
                ArchiveFormats.Add(vm);
            }
        }

        SelectedArchiveFormat = ArchiveFormats.FirstOrDefault();
        OnPropertyChanged(nameof(ArchiveFormatSummary));
    }

    public List<TransferHashAlgorithm> CollectHashAlgorithmOrder()
        => HashAlgorithms.Where(item => item.IsEnabled).Select(item => item.Value).ToList();

    public List<TransferArchiveFormat> CollectArchiveFormatOrder()
        => ArchiveFormats.Where(item => item.IsEnabled).Select(item => item.Value).ToList();

    [RelayCommand]
    private void MoveUpHashAlgorithm()
    {
        if (SelectedHashAlgorithm == null) return;
        int index = HashAlgorithms.IndexOf(SelectedHashAlgorithm);
        if (index > 0)
        {
            HashAlgorithms.Move(index, index - 1);
            OnPropertyChanged(nameof(HashAlgorithmSummary));
        }
    }

    [RelayCommand]
    private void MoveDownHashAlgorithm()
    {
        if (SelectedHashAlgorithm == null) return;
        int index = HashAlgorithms.IndexOf(SelectedHashAlgorithm);
        if (index >= 0 && index < HashAlgorithms.Count - 1)
        {
            HashAlgorithms.Move(index, index + 1);
            OnPropertyChanged(nameof(HashAlgorithmSummary));
        }
    }

    [RelayCommand]
    private void MoveUpArchiveFormat()
    {
        if (SelectedArchiveFormat == null) return;
        int index = ArchiveFormats.IndexOf(SelectedArchiveFormat);
        if (index > 0)
        {
            ArchiveFormats.Move(index, index - 1);
            OnPropertyChanged(nameof(ArchiveFormatSummary));
        }
    }

    [RelayCommand]
    private void MoveDownArchiveFormat()
    {
        if (SelectedArchiveFormat == null) return;
        int index = ArchiveFormats.IndexOf(SelectedArchiveFormat);
        if (index >= 0 && index < ArchiveFormats.Count - 1)
        {
            ArchiveFormats.Move(index, index + 1);
            OnPropertyChanged(nameof(ArchiveFormatSummary));
        }
    }

    [RelayCommand]
    private void DrillDownHashAlgorithms()
    {
        NavigateToSubPage(Strings.Get("Settings.FileTransfer.HashAlgorithmOrderTitle"), new HashAlgorithmsSubPage(this));
    }

    [RelayCommand]
    private void DrillDownArchiveFormats()
    {
        NavigateToSubPage(Strings.Get("Settings.FileTransfer.ArchiveFormatOrderTitle"), new ArchiveFormatsSubPage(this));
    }

    private static string FormatHashAlgorithm(TransferHashAlgorithm algorithm) => algorithm switch
    {
        TransferHashAlgorithm.Sha256 => "SHA-256",
        TransferHashAlgorithm.Sha1 => "SHA-1",
        TransferHashAlgorithm.Md5 => "MD5",
        _ => algorithm.ToString()
    };

    private static string FormatArchiveFormat(TransferArchiveFormat format) => format switch
    {
        TransferArchiveFormat.Zstd => "tar.zst (Zstandard)",
        TransferArchiveFormat.Gzip => "tar.gz (Gzip)",
        TransferArchiveFormat.Tar => Strings.Get("Settings.FileTransfer.ArchiveFormat.Tar"),
        _ => format.ToString()
    };
}

public class HashAlgorithmsSubPage : ViewModelBase
{
    public TransferSettingsPage Parent { get; }
    public HashAlgorithmsSubPage(TransferSettingsPage parent) => Parent = parent;
}

public class ArchiveFormatsSubPage : ViewModelBase
{
    public TransferSettingsPage Parent { get; }
    public ArchiveFormatsSubPage(TransferSettingsPage parent) => Parent = parent;
}
