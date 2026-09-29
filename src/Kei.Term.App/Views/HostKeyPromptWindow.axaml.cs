using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Security;

namespace Kei.Term.App.Views;

// 主机密钥确认框：关闭窗口 / Esc / 任何非显式选择一律视为拒绝
public partial class HostKeyPromptWindow : Window
{
    public HostKeyPromptWindow()
    {
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(HostKeyDecision.Reject);
            }
        };
    }

    public HostKeyPromptWindow(HostKeyEvaluation evaluation) : this()
    {
        PresentedHostKey key = evaluation.Presented;
        string endpoint = KnownHostMatcher.ToHostString(key.Host, key.Port);
        bool changed = evaluation.Verdict == HostKeyVerdict.Changed;

        Title = Strings.Get(changed ? "HostKeyPrompt.Title.Changed" : "HostKeyPrompt.Title.Unknown");
        KeyTypeText.Text = key.KeyType;
        FingerprintText.Text = key.FingerprintSha256;
        HintText.Text = Strings.Get(changed ? "HostKeyPrompt.Hint.Changed" : "HostKeyPrompt.Hint.Unknown");
        AcceptSaveButton.Content = Strings.Get(changed ? "HostKeyPrompt.ReplaceAndSave" : "HostKeyPrompt.AcceptAndSave");

        if (changed)
        {
            ChangedHeaderText.Text = string.Format(Strings.Get("HostKeyPrompt.Header.Changed"), endpoint);
            ChangedHeaderText.IsVisible = true;
            HeaderText.IsVisible = false;

            // 同算法的旧记录才是被"替换"的那一把；其它算法的记录不在此展示
            string previous = string.Join('\n', evaluation.KnownEntries
                .Where(e => e.Status == KnownHostStatus.Trusted && e.KeyType == key.KeyType)
                .Select(e => e.FingerprintSha256)
                .Distinct());
            PreviousLabel.IsVisible = previous.Length > 0;
            PreviousText.IsVisible = previous.Length > 0;
            PreviousText.Text = previous;
        }
        else
        {
            HeaderText.Text = string.Format(Strings.Get("HostKeyPrompt.Header.Unknown"), endpoint);
        }

        // 默认焦点：变更时落在「拒绝」，避免习惯性回车放行
        Opened += (_, _) => (changed ? RejectButton : AcceptSaveButton).Focus();
    }

    private void OnReject(object? sender, RoutedEventArgs e) => Close(HostKeyDecision.Reject);

    private void OnAcceptOnce(object? sender, RoutedEventArgs e) => Close(HostKeyDecision.AcceptOnce);

    private void OnAcceptAndSave(object? sender, RoutedEventArgs e) => Close(HostKeyDecision.AcceptAndRemember);
}
