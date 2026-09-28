using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kei.Term.Core.Models;

public enum NodeType
{
    Folder = 0,
    Session = 1,
    // 仅展示层，不落库
    VirtualRoot = 2
}

public abstract class TreeNodeBase : INotifyPropertyChanged
{
    private bool _isExpanded;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public abstract NodeType NodeType { get; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 展开状态提升至基类：消除 Avalonia 样式编译绑定的类型强转冲突，且叶子节点展开状态天然为 false
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class FolderNode : TreeNodeBase
{
    public override NodeType NodeType => NodeType.Folder;
    public List<TreeNodeBase> Children { get; set; } = [];
}

public class SessionNode : TreeNodeBase
{
    public override NodeType NodeType => NodeType.Session;
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Username { get; set; }
    // 绑定的身份；未绑定则回退全局默认身份/弹窗
    public Guid? IdentityId { get; set; }
    // 协议骨架，一期固定 ssh，多协议预留
    public string Protocol { get; set; } = "ssh";
    public string TerminalType { get; set; } = "xterm-256color";
    public string? StartupScript { get; set; }
    public Guid? JumpHostSessionId { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.Ordinal);
    // 可选指定的终端 Profile ID。若为 null 或空，则继承全局默认 TerminalProfile
    public string? TerminalProfileId { get; set; }

    // 文件传输协议偏好（SFTP / SCP）
    public FileTransferProtocol FileTransferProtocol { get; set; } = FileTransferProtocol.Sftp;
    // SFTP 连接模式（Auto / Subsystem / Dedicated）
    public SftpChannelMode SftpMode { get; set; } = SftpChannelMode.Auto;
}

// 展示层专用虚拟根（SecureCRT 式 "Sessions" 顶层）：不持久化，Id 固定 Empty 便于各入口守卫
public class VirtualRootNode : TreeNodeBase
{
    public VirtualRootNode()
    {
        Id = Guid.Empty;
        ParentId = null;
        Name = "Sessions";
        IsExpanded = true;
    }

    public override NodeType NodeType => NodeType.VirtualRoot;
    public List<TreeNodeBase> Children { get; set; } = [];
}

public record ResolvedSessionConfig(
    Guid SessionId,
    string SessionName,
    string Host,
    int Port,
    string Username,
    Guid? IdentityId,
    string TerminalType,
    string? StartupScript,
    Guid? JumpHostSessionId,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    FileTransferProtocol FileTransferProtocol = FileTransferProtocol.Sftp,
    SftpChannelMode SftpMode = SftpChannelMode.Auto
);
