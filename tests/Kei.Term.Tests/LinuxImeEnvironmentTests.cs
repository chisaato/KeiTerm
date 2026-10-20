using Kei.Term.App.Services;

namespace Kei.Term.Tests;

public class LinuxImeEnvironmentTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values)
        => name => values.TryGetValue(name, out string? value) ? value : null;

    [Fact]
    public void Resolve_NoImEnvironmentAndFcitx5Running_SelectsFcitx()
    {
        string? module = LinuxImeEnvironment.Resolve(Env(new()), ["systemd", "fcitx5", "kwin_wayland"]);

        Assert.Equal("fcitx", module);
    }

    [Fact]
    public void Resolve_FcitxAndIbusBothRunning_PrefersFcitx()
    {
        string? module = LinuxImeEnvironment.Resolve(Env(new()), ["ibus-daemon", "fcitx5"]);

        Assert.Equal("fcitx", module);
    }

    [Fact]
    public void Resolve_OnlyIbusRunning_SelectsIbus()
    {
        string? module = LinuxImeEnvironment.Resolve(Env(new()), ["ibus-daemon"]);

        Assert.Equal("ibus", module);
    }

    [Fact]
    public void Resolve_NoDaemonRunning_LeavesEnvironmentUntouched()
    {
        Assert.Null(LinuxImeEnvironment.Resolve(Env(new()), ["systemd", "plasmashell"]));
    }

    [Theory]
    [InlineData("AVALONIA_IM_MODULE", "none")]
    [InlineData("GTK_IM_MODULE", "ibus")]
    [InlineData("QT_IM_MODULE", "xim")]
    [InlineData("XMODIFIERS", "@im=fcitx")]
    public void Resolve_UserDeclaredInputMethod_IsRespected(string variable, string value)
    {
        string? module = LinuxImeEnvironment.Resolve(Env(new() { [variable] = value }), ["fcitx5"]);

        Assert.Null(module);
    }

    [Fact]
    public void Resolve_XModifiersWithoutImEntry_StillDetects()
    {
        string? module = LinuxImeEnvironment.Resolve(Env(new() { ["XMODIFIERS"] = "" }), ["fcitx5"]);

        Assert.Equal("fcitx", module);
    }
}
