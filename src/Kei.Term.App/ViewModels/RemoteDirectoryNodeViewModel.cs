namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Microsoft.Extensions.Logging;

// 远程目录树节点（仅包含文件夹/目录，类似 SecureCRT 左侧目录树）
public partial class RemoteDirectoryNodeViewModel : ViewModelBase
{
    private readonly IRemoteFileSystem _fileSystem;
    private readonly Func<bool> _showHiddenFilesFunc;
    private bool _hasLoadedChildren;

    private static Microsoft.Extensions.Logging.ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    // 注入静态日志器（由 ViewModel 构造时设置一次）
    public static void SetLogger(Microsoft.Extensions.Logging.ILogger logger) => _logger = logger;
    public string Name { get; }
    public string FullPath { get; }

    [ObservableProperty]
    private bool _hasChildren = true;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<RemoteDirectoryNodeViewModel> Children { get; } = [];

    public RemoteDirectoryNodeViewModel(
        string name,
        string fullPath,
        IRemoteFileSystem fileSystem,
        Func<bool> showHiddenFilesFunc,
        bool hasDummyChild = true)
    {
        Name = name;
        FullPath = fullPath;
        _fileSystem = fileSystem;
        _showHiddenFilesFunc = showHiddenFilesFunc;

        if (hasDummyChild)
        {
            Children.Add(CreateDummyChild());
        }
    }

    private static RemoteDirectoryNodeViewModel CreateDummyChild()
    {
        return new RemoteDirectoryNodeViewModel(string.Empty, string.Empty, null!, () => false, hasDummyChild: false);
    }

    public bool IsDummy => _fileSystem == null || string.IsNullOrEmpty(Name);

    public bool HasLoadedChildren => _hasLoadedChildren;

    partial void OnIsExpandedChanged(bool value)
    {
        _logger.LogDebug("TreeVM OnIsExpandedChanged: {Path} -> {Value} (loaded={Loaded})", FullPath, value, _hasLoadedChildren);
        if (value && !_hasLoadedChildren && _fileSystem != null)
        {
            _ = LoadChildrenAsync();
        }
    }

    // 进行中的加载任务：并发调用方共享等待同一次加载，而不是被忙碌保护丢弃
    private Task? _childrenLoadTask;

    public Task LoadChildrenAsync(CancellationToken ct = default)
    {
        if (_fileSystem == null) return Task.CompletedTask;

        // 若已有加载在途，直接复用同一个任务，确保调用方等待真实完成而不是拿到空 Children
        if (_childrenLoadTask is { IsCompleted: false })
        {
            return _childrenLoadTask;
        }

        _childrenLoadTask = LoadChildrenCoreAsync(ct);
        return _childrenLoadTask;
    }

    private async Task LoadChildrenCoreAsync(CancellationToken ct)
    {
        try
        {
            IsLoading = true;
            _logger.LogDebug("TreeVM LoadChildrenAsync begin: {Path}", FullPath);
            var remoteItems = await _fileSystem.ListDirectoryAsync(FullPath, ct);

            bool showHidden = _showHiddenFilesFunc();
            var dirItems = remoteItems
                .Where(x => x.IsDirectory && (showHidden || !x.Name.StartsWith('.')))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                {
                    // 构造规范的绝对子路径
                    string childPath = FullPath == "/" ? "/" + x.Name : FullPath.TrimEnd('/') + "/" + x.Name;
                    return new RemoteDirectoryNodeViewModel(x.Name, childPath, _fileSystem, _showHiddenFilesFunc, hasDummyChild: true);
                })
                .ToList();

            Children.Clear();
            foreach (var child in dirItems)
            {
                Children.Add(child);
            }
            _hasLoadedChildren = true;
            HasChildren = Children.Count > 0;
            _logger.LogDebug("TreeVM LoadChildrenAsync done: {Path} -> {Count} dirs", FullPath, Children.Count);
            OnPropertyChanged(nameof(Children));
        }
        catch
        {
            Children.Clear();
            _hasLoadedChildren = true;
            HasChildren = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<RemoteDirectoryNodeViewModel?> EnsurePathLoadedAsync(string path, CancellationToken ct = default)
    {
        string currentNorm = FullPath.TrimEnd('/');
        string targetNorm = path.TrimEnd('/');
        if (string.IsNullOrEmpty(currentNorm)) currentNorm = "/";
        if (string.IsNullOrEmpty(targetNorm)) targetNorm = "/";

        _logger.LogDebug("TreeVM EnsurePathLoadedAsync: root={Root} target={Target}", FullPath, path);

        if (currentNorm == targetNorm)
        {
            IsExpanded = true;
            return this;
        }

        // 分割目标路径段，逐步确保展开
        string[] segments = targetNorm.Split('/', StringSplitOptions.RemoveEmptyEntries);

        RemoteDirectoryNodeViewModel currentNode = this;
        currentNode.IsExpanded = true;

        foreach (var segment in segments)
        {
            if (!currentNode._hasLoadedChildren)
            {
                await currentNode.LoadChildrenAsync(ct);
            }
            currentNode.IsExpanded = true;

            // 过滤虚拟占位子节点与空节点，避免 c.Name 为 null 导致 NullReferenceException
            var matchedChild = currentNode.Children
                .FirstOrDefault(c => c != null && !c.IsDummy && !string.IsNullOrEmpty(c.Name) && string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (matchedChild == null)
            {
                _logger.LogDebug("TreeVM EnsurePathLoadedAsync: segment '{Segment}' not found under {Path} (children={Count}), retry once", segment, currentNode.FullPath, currentNode.Children.Count);
                // 重新刷新再试一次
                await currentNode.LoadChildrenAsync(ct);
                matchedChild = currentNode.Children
                    .FirstOrDefault(c => c != null && !c.IsDummy && !string.IsNullOrEmpty(c.Name) && string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            }

            if (matchedChild != null)
            {
                _logger.LogDebug("TreeVM EnsurePathLoadedAsync: matched '{Segment}' -> {ChildPath}", segment, matchedChild.FullPath);
                // 像 SecureCRT 一样单向跟踪：收起同级的其它兄弟目录，只展开匹配的目标子分支
                foreach (var sibling in currentNode.Children)
                {
                    if (sibling != null && !ReferenceEquals(sibling, matchedChild))
                    {
                        sibling.IsExpanded = false;
                    }
                }

                matchedChild.IsExpanded = true;
                if (!matchedChild._hasLoadedChildren)
                {
                    await matchedChild.LoadChildrenAsync(ct);
                }
                currentNode = matchedChild;
            }
            else
            {
                _logger.LogDebug("TreeVM EnsurePathLoadedAsync: segment '{Segment}' NOT matched under {Path}, stop descent", segment, currentNode.FullPath);
                break;
            }
        }

        _logger.LogDebug("TreeVM EnsurePathLoadedAsync: resolved to {ResolvedPath} (target={Target})", currentNode.FullPath, path);
        return currentNode;
    }
}
