using System.Globalization;
using System.Net;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

public partial class TerminalTabViewModel
{
    private string _titleTemplate = string.Empty;
    private bool _hasManualTitle;

    public TabTitleContext TitleContext => new(
        TabName, Config?.Host, Config?.Port.ToString(CultureInfo.InvariantCulture), Config?.Username,
        _directoryTracker.ReportedHost, _terminalWorkingDirectory,
        State == ConnectionState.Connected && IPAddress.TryParse(Config?.Host, out IPAddress? address) ? address.ToString() : null,
        RemoteTitle);

    public void ApplyTitleTemplate(string? template)
    {
        _titleTemplate = template ?? string.Empty;
        UpdateTitle();
    }

    internal string FormatFileTabTitle(string path)
        => RenderTitleTemplate(TitleContext with { RemotePath = path }) ?? Helpers.WorkspaceTabTitle.ForDirectory(TabName, path);

    private string? RenderTitleTemplate(TabTitleContext context)
        => !string.IsNullOrWhiteSpace(_titleTemplate)
            && TabTitleTemplate.TryRender(_titleTemplate, context, out string title, out _)
            && !string.IsNullOrWhiteSpace(title) ? title : null;
}
