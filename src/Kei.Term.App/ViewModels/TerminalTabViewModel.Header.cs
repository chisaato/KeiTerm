namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoyalTerminal.Avalonia.Controls;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Ssh.Abstractions;

// 标签右键菜单中需要主窗口处理的动作（涉及其他标签或连接编排）
public enum TabAction
{
    Reconnect,
    Clone,
    Rename,
    CloseOthers,
    CloseToRight
}

// 标签头：标题（会话名 / 远端标题 / 重命名）、悬停提示、活动标记与右键动作
public partial class TerminalTabViewModel
{
    // 远端标题最长保留字符数：防止异常程序刷出超长标题撑爆标签条与提示框
    private const int MaxRemoteTitleLength = 256;

    // 请求关闭此标签页（如会话正常退出时触发）
    public event Action<TerminalTabViewModel>? CloseRequested;

    // 标签右键菜单动作，由主窗口 VM 订阅处理
    public event Action<TerminalTabViewModel, TabAction>? ActionRequested;

    // 显示在标签上的标题：跟随远端时为远端标题（未设置则回退标签名），否则为标签名
    [ObservableProperty]
    private string _title = Strings.Get("Main.Tab.DefaultTitle");

    // 标签名：默认取会话名，可在标签上重命名（仅本标签生效，不改会话）
    [ObservableProperty]
    private string _tabName = Strings.Get("Main.Tab.DefaultTitle");

    // 远端程序通过 OSC 0/2 设置的最近一次标题
    [ObservableProperty]
    private string? _remoteTitle;

    // 本标签是否用远端标题；初值来自会话/全局设置，可在标签右键菜单临时切换
    [ObservableProperty]
    private bool _followRemoteTitle;

    // 后台标签收到新输出或响铃：标签上显示活动标记，切到该标签时清除
    [ObservableProperty]
    private bool _hasActivity;

    // 标签悬停提示：会话、目标地址、状态
    [ObservableProperty]
    private string _toolTip = string.Empty;

    // 发起本标签连接时的解析配置（重连 / 克隆 / 提示信息用）；未经连接编排创建的标签为 null
    public ResolvedSessionConfig? Config { get; private set; }

    partial void OnTabNameChanged(string value) => UpdateTitle();

    partial void OnRemoteTitleChanged(string? value) => UpdateTitle();

    partial void OnFollowRemoteTitleChanged(bool value) => UpdateTitle();

    partial void OnStatusMessageChanged(string? value) => UpdateToolTip();

    partial void OnTitleChanged(string value) => UpdateToolTip();

    private void UpdateTitle()
        => Title = FollowRemoteTitle && !string.IsNullOrWhiteSpace(RemoteTitle) ? RemoteTitle! : TabName;

    private void UpdateToolTip()
    {
        var lines = new List<string> { Title };
        if (!string.Equals(Title, TabName, StringComparison.Ordinal))
        {
            lines.Add(Strings.Format("Main.Tab.ToolTip.Session", TabName));
        }

        if (Config is { } config)
        {
            lines.Add($"{config.Username}@{config.Host}:{config.Port}");
        }

        if (!string.IsNullOrWhiteSpace(StatusMessage))
        {
            lines.Add(StatusMessage!);
        }

        ToolTip = string.Join(Environment.NewLine, lines);
    }

    // 记录连接配置并按其行为设置初始化标题跟随（连接编排开标签时调用）
    public void BindConfig(ResolvedSessionConfig config)
    {
        // 标题跟随只在首次绑定时取会话/全局设置；重连沿用用户在本标签上的选择（含重命名后的停止跟随）
        if (Config == null)
        {
            FollowRemoteTitle = config.FollowRemoteTitle;
        }

        Config = config;
        UpdateToolTip();
    }

    // 远端标题：去掉控制字符并截断；空标题表示远端清除了标题
    public static string? SanitizeRemoteTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, MaxRemoteTitleLength));
        foreach (char c in raw)
        {
            if (builder.Length >= MaxRemoteTitleLength)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public void ApplyRemoteTitle(string? raw) => RemoteTitle = SanitizeRemoteTitle(raw);

    // 重命名：用户给定的名字优先于远端标题，因此同时停止跟随
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        TabName = name.Trim();
        FollowRemoteTitle = false;
    }

    [RelayCommand]
    private void ToggleFollowRemoteTitle() => FollowRemoteTitle = !FollowRemoteTitle;

    [RelayCommand]
    private void RequestReconnect() => ActionRequested?.Invoke(this, TabAction.Reconnect);

    [RelayCommand]
    private void RequestClone() => ActionRequested?.Invoke(this, TabAction.Clone);

    [RelayCommand]
    private void RequestRename() => ActionRequested?.Invoke(this, TabAction.Rename);

    [RelayCommand]
    private void RequestCloseOthers() => ActionRequested?.Invoke(this, TabAction.CloseOthers);

    [RelayCommand]
    private void RequestCloseToRight() => ActionRequested?.Invoke(this, TabAction.CloseToRight);

    [RelayCommand]
    private void RequestClose() => CloseRequested?.Invoke(this);


    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            HasActivity = false;
        }
    }

    // 后台线程上的输出回调：只在状态需要翻转时才投递到 UI 线程，避免每个数据块都排队
    private void OnSessionOutput(byte[] data)
    {
        if (data.Length > 0) _hasRemoteOutput = true;
        if (_activityPending || IsSelected || HasActivity)
        {
            return;
        }

        _activityPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _activityPending = false;
            if (!IsSelected)
            {
                HasActivity = true;
            }
        });
    }

    private volatile bool _activityPending;
    private volatile bool _hasRemoteOutput;

    private void OnRemoteBell()
    {
        if (!IsSelected)
        {
            HasActivity = true;
        }
    }
}
