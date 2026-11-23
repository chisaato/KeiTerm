using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Converters;

public sealed class LocalizedEnumDisplayConverter : IValueConverter
{
    public static readonly LocalizedEnumDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            FileTransferProtocol.Sftp => "SFTP",
            FileTransferProtocol.Scp => "SCP",
            SftpChannelMode.Auto => Strings.Get("Enum.SftpChannelMode.Auto"),
            SftpChannelMode.Subsystem => Strings.Get("Enum.SftpChannelMode.Subsystem"),
            SftpChannelMode.Dedicated => Strings.Get("Enum.SftpChannelMode.Dedicated"),
            PortForwardMode.Local => Strings.Get("Enum.PortForwardMode.Local"),
            PortForwardMode.Remote => Strings.Get("Enum.PortForwardMode.Remote"),
            PortForwardMode.Dynamic => Strings.Get("Enum.PortForwardMode.Dynamic"),
            SerialParity.None => Strings.Get("Common.None"),
            SerialParity.Odd => Strings.Get("Enum.SerialParity.Odd"),
            SerialParity.Even => Strings.Get("Enum.SerialParity.Even"),
            SerialParity.Mark => Strings.Get("Enum.SerialParity.Mark"),
            SerialParity.Space => Strings.Get("Enum.SerialParity.Space"),
            SerialStopBits.One => "1",
            SerialStopBits.OnePointFive => "1.5",
            SerialStopBits.Two => "2",
            "Auto" => Strings.Get("Enum.WatchMode.Auto"),
            "OSNative" => Strings.Get("Enum.WatchMode.OSNative"),
            "Polling" => Strings.Get("Enum.WatchMode.Polling"),
            "macos" => "macOS",
            "windows" => "Windows",
            "linux" => "Linux",
            "any" => Strings.Get("Enum.Platform.Any"),
            _ => value?.ToString() ?? string.Empty
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
