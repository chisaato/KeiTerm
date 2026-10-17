namespace Kei.Term.App.ViewModels.Settings;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.Core.Transfers;

public partial class TransferOrderedItemViewModel<T> : ViewModelBase
    where T : struct, Enum
{
    public T Value { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public TransferOrderedItemViewModel(T value, string displayName, bool isEnabled)
    {
        Value = value;
        DisplayName = displayName;
        _isEnabled = isEnabled;
    }
}

public partial class FileTransferSettingsPage
{
    [ObservableProperty]
    private bool _verifyHashEnabled = true;

    [ObservableProperty]
    private bool _archiveDirectoriesEnabled;

    public ObservableCollection<TransferOrderedItemViewModel<TransferHashAlgorithm>> HashAlgorithms { get; } = [];
    public ObservableCollection<TransferOrderedItemViewModel<TransferArchiveFormat>> ArchiveFormats { get; } = [];

    [ObservableProperty]
    private TransferOrderedItemViewModel<TransferHashAlgorithm>? _selectedHashAlgorithm;

    [ObservableProperty]
    private TransferOrderedItemViewModel<TransferArchiveFormat>? _selectedArchiveFormat;

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
        List<TransferHashAlgorithm> effective = [];
        HashSet<TransferHashAlgorithm> seen = [];

        if (order != null)
        {
            foreach (TransferHashAlgorithm item in order)
            {
                if (Enum.IsDefined(item) && seen.Add(item))
                {
                    effective.Add(item);
                    HashAlgorithms.Add(new TransferOrderedItemViewModel<TransferHashAlgorithm>(item, FormatHashAlgorithm(item), true));
                }
            }
        }

        foreach (TransferHashAlgorithm item in defined)
        {
            if (seen.Add(item))
            {
                HashAlgorithms.Add(new TransferOrderedItemViewModel<TransferHashAlgorithm>(item, FormatHashAlgorithm(item), false));
            }
        }

        SelectedHashAlgorithm = HashAlgorithms.FirstOrDefault();
    }

    private void PopulateArchiveFormats(IReadOnlyList<TransferArchiveFormat>? order)
    {
        ArchiveFormats.Clear();
        List<TransferArchiveFormat> defined = Enum.GetValues<TransferArchiveFormat>().ToList();
        List<TransferArchiveFormat> effective = [];
        HashSet<TransferArchiveFormat> seen = [];

        if (order != null)
        {
            foreach (TransferArchiveFormat item in order)
            {
                if (Enum.IsDefined(item) && seen.Add(item))
                {
                    effective.Add(item);
                    ArchiveFormats.Add(new TransferOrderedItemViewModel<TransferArchiveFormat>(item, FormatArchiveFormat(item), true));
                }
            }
        }

        foreach (TransferArchiveFormat item in defined)
        {
            if (seen.Add(item))
            {
                ArchiveFormats.Add(new TransferOrderedItemViewModel<TransferArchiveFormat>(item, FormatArchiveFormat(item), false));
            }
        }

        SelectedArchiveFormat = ArchiveFormats.FirstOrDefault();
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
        }
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
        TransferArchiveFormat.Tar => "tar (无压缩)",
        _ => format.ToString()
    };
}
