using System.Collections.Generic;
using Kei.Term.Core.Models.Profiles.BuiltIns.Gui;
using Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

namespace Kei.Term.Core.Models.Profiles;

public static class BuiltInPresets
{
    // 内置 GUI 外观预置 ID（向后兼容常量）
    public const string GuiVsCodeDarkId = VsCodeDarkGuiPreset.Id;
    public const string GuiOneDarkId = OneDarkGuiPreset.Id;
    public const string GuiNordId = NordGuiPreset.Id;
    public const string GuiSolarizedDarkId = SolarizedDarkGuiPreset.Id;
    public const string GuiVeritasHareId = VeritasHareGuiPreset.Id;
    public const string GuiVeritasChihiroId = VeritasChihiroGuiPreset.Id;
    public const string GuiVeritasMakiId = VeritasMakiGuiPreset.Id;
    public const string GuiVeritasKotamaId = VeritasKotamaGuiPreset.Id;

    // 内置 Terminal 终端预置 ID（向后兼容常量）
    public const string TerminalMonokaiId = MonokaiTerminalPreset.Id;
    public const string TerminalDraculaId = DraculaTerminalPreset.Id;
    public const string TerminalGruvboxDarkId = GruvboxTerminalPreset.Id;
    public const string TerminalSolarizedDarkId = SolarizedDarkTerminalPreset.Id;
    public const string TerminalSolarizedLightId = SolarizedLightTerminalPreset.Id;
    public const string TerminalTomorrowNightId = TomorrowNightTerminalPreset.Id;
    public const string TerminalVeritasHareId = VeritasHareTerminalPreset.Id;
    public const string TerminalVeritasChihiroId = VeritasChihiroTerminalPreset.Id;
    public const string TerminalVeritasMakiId = VeritasMakiTerminalPreset.Id;
    public const string TerminalVeritasKotamaId = VeritasKotamaTerminalPreset.Id;

    // 默认内置 GUI 主题聚合列表
    public static IReadOnlyList<GuiProfile> DefaultGuiProfiles { get; } = new List<GuiProfile>
    {
        VsCodeDarkGuiPreset.Instance,
        OneDarkGuiPreset.Instance,
        NordGuiPreset.Instance,
        SolarizedDarkGuiPreset.Instance,
        VeritasHareGuiPreset.Instance,
        VeritasChihiroGuiPreset.Instance,
        VeritasMakiGuiPreset.Instance,
        VeritasKotamaGuiPreset.Instance
    }.AsReadOnly();

    // 默认内置 Terminal 终端主题聚合列表
    public static IReadOnlyList<TerminalProfile> DefaultTerminalProfiles { get; } = new List<TerminalProfile>
    {
        MonokaiTerminalPreset.Instance,
        DraculaTerminalPreset.Instance,
        GruvboxTerminalPreset.Instance,
        SolarizedDarkTerminalPreset.Instance,
        SolarizedLightTerminalPreset.Instance,
        TomorrowNightTerminalPreset.Instance,
        VeritasHareTerminalPreset.Instance,
        VeritasChihiroTerminalPreset.Instance,
        VeritasMakiTerminalPreset.Instance,
        VeritasKotamaTerminalPreset.Instance
    }.AsReadOnly();

    public static GuiProfile GetDefaultGuiProfile() => DefaultGuiProfiles[0];

    public static TerminalProfile GetDefaultTerminalProfile() => DefaultTerminalProfiles[0];
}
