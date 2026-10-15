using System.Collections.Generic;
using System.Linq;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public sealed record TerminalUploadOption(TerminalUploadProtocol Protocol, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record TerminalDropActionOption(TerminalFileDropAction? Action, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class TerminalFileDropOptions
{
    public static IReadOnlyList<TerminalUploadOption> UploadProtocols { get; } =
    [
        new(TerminalUploadProtocol.ZModem, "Z Modem"),
        new(TerminalUploadProtocol.YModem, "Y Modem"),
        new(TerminalUploadProtocol.Scp, "SCP"),
        new(TerminalUploadProtocol.Sftp, "SFTP")
    ];

    public static IReadOnlyList<TerminalDropActionOption> DropActions { get; } =
    [
        new(null, Strings.Get("Terminal.Drop.Ask")),
        new(TerminalFileDropAction.Upload, Strings.Get("Terminal.Drop.Upload")),
        new(TerminalFileDropAction.PastePath, Strings.Get("Terminal.Drop.PastePath")),
        new(TerminalFileDropAction.PasteFileName, Strings.Get("Terminal.Drop.PasteFileName"))
    ];

    public static IReadOnlyList<InheritableOption<TerminalUploadProtocol>> InheritableUploadProtocols(TerminalUploadProtocol global)
        => [new(null, Strings.Format("Behavior.Inherit", Describe(global))),
            .. UploadProtocols.Select(option => new InheritableOption<TerminalUploadProtocol>(option.Protocol, option.DisplayName))];

    public static string Describe(TerminalUploadProtocol protocol)
        => UploadProtocols.FirstOrDefault(option => option.Protocol == protocol)?.DisplayName ?? "SCP";
}
