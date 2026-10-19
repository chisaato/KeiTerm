using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    internal static bool TryNormalizeName(string? input, out string name)
    {
        name = input?.Trim() ?? string.Empty;
        return name.Length > 0 && name is not "." and not ".." && !name.Contains('/') && !name.Contains('\0');
    }

    internal static string JoinRemotePath(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    [RelayCommand]
    public Task CreateDirectoryAsync() => CreateEntryAsync(true);

    [RelayCommand]
    public Task CreateFileAsync() => CreateEntryAsync(false);

    private async Task CreateEntryAsync(bool directory)
    {
        try
        {
            string parent = CurrentPath;
            string? input = await (_interaction ?? NullInteractionService.Instance).PromptTextAsync(
                Strings.Get(directory ? "FileManager.CreateDirectory" : "FileManager.CreateFile"),
                Strings.Get("FileManager.Name"), null);
            if (input == null) return;
            if (!TryNormalizeName(input, out string name))
            {
                TransferStatusMessage = Strings.Get("FileManager.InvalidName");
                return;
            }
            string path = JoinRemotePath(parent, name);
            if (await _fileSystem.GetAttributesAsync(path) != null)
            {
                TransferStatusMessage = Strings.Get("FileManager.TargetExists");
                return;
            }
            if (directory) await _fileSystem.CreateDirectoryAsync(path);
            else
            {
                await using Stream stream = await _fileSystem.OpenWriteAsync(path);
                await _fileSystem.CommitWriteAsync(stream);
            }
            await RefreshDirectoryAsync();
        }
        catch (Exception ex) { ReportOperationFailure(ex); }
    }

    [RelayCommand]
    public async Task RenameItemAsync(RemoteFileItem? item)
    {
        if (item == null || item.Name == ".." || SelectedItems.Count > 1) return;
        try
        {
            string? input = await (_interaction ?? NullInteractionService.Instance).PromptTextAsync(
                Strings.Get("FileManager.Rename"), Strings.Get("FileManager.Name"), item.Name);
            if (input == null) return;
            if (!TryNormalizeName(input, out string name))
            {
                TransferStatusMessage = Strings.Get("FileManager.InvalidName");
                return;
            }
            if (name == item.Name) return;
            int separator = item.FullPath.LastIndexOf('/');
            string path = JoinRemotePath(item.FullPath[..Math.Max(separator, 0)], name);
            if (await _fileSystem.GetAttributesAsync(path) != null)
            {
                TransferStatusMessage = Strings.Get("FileManager.TargetExists");
                return;
            }
            await _fileSystem.RenameAsync(item.FullPath, path);
            await RefreshDirectoryAsync();
        }
        catch (Exception ex) { ReportOperationFailure(ex); }
    }

    [RelayCommand]
    public async Task UploadFilesAsync()
    {
        try
        {
            var paths = await (_interaction ?? NullInteractionService.Instance).PickFilesAsync(Strings.Get("FileManager.UploadFiles"));
            if (paths is { Count: > 0 }) await UploadLocalFilesAsync(paths.ToArray());
        }
        catch (Exception ex) { ReportOperationFailure(ex); }
    }

    private void ReportOperationFailure(Exception ex)
    {
        _logger.LogError(ex, "Remote file operation failed");
        TransferStatusMessage = string.Format(Strings.Get("FileManager.OperationFailed"), ex.Message);
    }
}
