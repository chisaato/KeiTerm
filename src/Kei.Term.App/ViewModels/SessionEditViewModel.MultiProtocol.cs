using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;

namespace Kei.Term.App.ViewModels;

public partial class SessionEditViewModel
{
    private DeviceConfigurationService? _deviceConfigService;
    private ISyncDeviceId? _syncDeviceId;
    private bool _initializingProtocol;
    private bool _flowControlRejected;
    private ISerialPortCatalog? _serialPortCatalog;

    [ObservableProperty] private ObservableCollection<string> _serialPortNames = [];
    [ObservableProperty] private string _serialPortDiscoveryError = string.Empty;

    [RelayCommand]
    private async Task RefreshSerialPortsAsync(CancellationToken ct)
    {
        SerialPortDiscoveryError = string.Empty;
        try
        {
            if (_serialPortCatalog is null) throw new NotSupportedException("串口发现服务不可用，仍可手动输入端口路径。");
            var names = await _serialPortCatalog.GetPortNamesAsync(SelectedTransportEngine?.Value ?? _globalSettings.TransportEngine, ct);
            ct.ThrowIfCancellationRequested();
            SerialPortNames = new ObservableCollection<string>(names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { SerialPortDiscoveryError = ex.Message; }
    }

    // 各作用域显式设置的字段字典（深拷贝自存储，修改时仅改用户显式设置的键）
    private readonly Dictionary<string, JsonElement> _osExplicitFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _deviceExplicitFields = new(StringComparer.Ordinal);

    // 所有可用连接协议
    [ObservableProperty]
    private ObservableCollection<ConnectionProtocolOption> _availableConnectionProtocols = [];

    [ObservableProperty]
    private ConnectionProtocolOption _selectedConnectionProtocolOption = SessionBehaviorOptions.ConnectionProtocols[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSshProtocol))]
    [NotifyPropertyChangedFor(nameof(IsTelnetProtocol))]
    [NotifyPropertyChangedFor(nameof(IsSerialProtocol))]
    [NotifyPropertyChangedFor(nameof(IsNetworkProtocol))]
    [NotifyPropertyChangedFor(nameof(IsUnsupportedProtocol))]
    [NotifyPropertyChangedFor(nameof(HasFileTransferSupport))]
    private string _selectedConnectionProtocol = SessionProtocols.Ssh;

    public bool IsSshProtocol => SelectedConnectionProtocol == SessionProtocols.Ssh;
    public bool IsTelnetProtocol => SelectedConnectionProtocol == SessionProtocols.Telnet;
    public bool IsSerialProtocol => SelectedConnectionProtocol == SessionProtocols.Serial;
    public bool IsNetworkProtocol => IsSshProtocol || IsTelnetProtocol;
    public bool IsUnsupportedProtocol => !IsSshProtocol && !IsTelnetProtocol && !IsSerialProtocol;
    public bool HasFileTransferSupport => IsSshProtocol;

    // 串口作用域选择
    public IReadOnlyList<SerialScopeOption> SerialScopes => SessionBehaviorOptions.SerialScopes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSerialScope))]
    [NotifyPropertyChangedFor(nameof(IsGenericScopeSelected))]
    [NotifyPropertyChangedFor(nameof(IsOsScopeSelected))]
    [NotifyPropertyChangedFor(nameof(IsDeviceScopeSelected))]
    [NotifyPropertyChangedFor(nameof(IsOverrideScopeSelected))]
    [NotifyPropertyChangedFor(nameof(CurrentScopeDescription))]
    private SerialScopeOption _selectedSerialScopeOption = SessionBehaviorOptions.SerialScopes[0];

    public SerialScope CurrentSerialScope => SelectedSerialScopeOption?.Scope ?? SerialScope.Generic;
    public bool IsGenericScopeSelected => CurrentSerialScope == SerialScope.Generic;
    public bool IsOsScopeSelected => CurrentSerialScope == SerialScope.OperatingSystem;
    public bool IsDeviceScopeSelected => CurrentSerialScope == SerialScope.Device;
    public bool IsOverrideScopeSelected => !IsGenericScopeSelected;

    public string CurrentScopeDescription => CurrentSerialScope switch
    {
        SerialScope.OperatingSystem => string.Format(Strings.Get("SessionEdit.Serial.Scope.Os"), CurrentOperatingSystemName),
        SerialScope.Device => Strings.Get("SessionEdit.Serial.Scope.Device"),
        _ => Strings.Get("SessionEdit.Serial.Scope.Generic")
    };

    // 通用串口参数（Generic Defaults）
    public IReadOnlyList<int> SerialBaudRates => SessionBehaviorOptions.SerialBaudRates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentScopeBaudRate))]
    [NotifyPropertyChangedFor(nameof(EffectiveSerialBaudRate))]
    private int _serialBaudRate = 115200;

    public IReadOnlyList<int> SerialDataBitsOptions => SessionBehaviorOptions.SerialDataBits;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentScopeDataBits))]
    private int _serialDataBits = 8;

    public IReadOnlyList<SerialParity> SerialParities => SessionBehaviorOptions.SerialParities;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentScopeParity))]
    private SerialParity _serialParity = SerialParity.None;

    public IReadOnlyList<SerialStopBits> SerialStopBitsOptions => SessionBehaviorOptions.SerialStopBitsOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentScopeStopBits))]
    private SerialStopBits _serialStopBits = SerialStopBits.One;

    public IReadOnlyList<SerialFlowControlOption> SerialFlowControls => SessionBehaviorOptions.SerialFlowControls;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SerialFlowControl))]
    [NotifyPropertyChangedFor(nameof(CurrentScopeFlowControl))]
    [NotifyPropertyChangedFor(nameof(IsSerialRtsConflict))]
    [NotifyPropertyChangedFor(nameof(IsSerialRtsManualAllowed))]
    private SerialFlowControlOption _selectedSerialFlowControl = SessionBehaviorOptions.SerialFlowControls[0];

    public SerialFlowControl SerialFlowControl => SelectedSerialFlowControl?.Mode ?? SerialFlowControl.None;

    // DTR & RTS 引脚参数
    public IReadOnlyList<SerialPinOption> SerialPinOptions => SessionBehaviorOptions.SerialPinOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentScopeDtrEnable))]
    private bool? _serialDtrEnable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentScopeRtsEnable))]
    private bool? _serialRtsEnable;

    // 当前作用域解析的有效流控
    public SerialFlowControl EffectiveScopeFlowControl
    {
        get
        {
            if (IsDeviceScopeSelected && _deviceExplicitFields.TryGetValue("flowControl", out var devFc) && devFc.ValueKind == JsonValueKind.String
                && Enum.TryParse<SerialFlowControl>(devFc.GetString(), out var fc1))
                return fc1;
            if ((IsDeviceScopeSelected || IsOsScopeSelected) && _osExplicitFields.TryGetValue("flowControl", out var osFc) && osFc.ValueKind == JsonValueKind.String
                && Enum.TryParse<SerialFlowControl>(osFc.GetString(), out var fc2))
                return fc2;
            return SerialFlowControl;
        }
    }

    public bool IsSerialRtsConflict => EffectiveScopeFlowControl == SerialFlowControl.RtsCts;
    public bool IsSerialRtsManualAllowed => !IsSerialRtsConflict;
    public string SerialRtsConflictWarning => Strings.Get("SessionEdit.Serial.RtsConflict");

    // 下层手动 RTS 与上层硬件流控冲突检测
    public bool IsLowerLevelManualRtsConflict
    {
        get
        {
            if (_flowControlRejected) return true;
            if (EffectiveScopeFlowControl != SerialFlowControl.RtsCts) return false;
            if (IsDeviceScopeSelected)
            {
                if (_osExplicitFields.TryGetValue("rtsEnable", out var osRts) && osRts.ValueKind is JsonValueKind.True or JsonValueKind.False) return true;
                if (SerialRtsEnable.HasValue) return true;
            }
            else if (IsOsScopeSelected)
            {
                if (SerialRtsEnable.HasValue) return true;
            }
            return false;
        }
    }

    public string LowerLevelRtsConflictWarning => Strings.Get("SessionEdit.Serial.LowerRtsConflict");

    // 三级端口绑定：通用值、当前 OS 默认值、当前设备专属覆盖
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSerialPortName))]
    [NotifyPropertyChangedFor(nameof(CurrentScopePortName))]
    private string _serialGenericPort = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSerialPortName))]
    [NotifyPropertyChangedFor(nameof(CurrentScopePortName))]
    private string _serialOsPort = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSerialPortName))]
    [NotifyPropertyChangedFor(nameof(CurrentScopePortName))]
    private string _serialDevicePort = string.Empty;

    public string EffectiveSerialPortName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SerialDevicePort)) return SerialDevicePort.Trim();
            if (!string.IsNullOrWhiteSpace(SerialOsPort)) return SerialOsPort.Trim();
            return SerialGenericPort.Trim();
        }
    }

    public int EffectiveSerialBaudRate
    {
        get
        {
            if (_deviceExplicitFields.TryGetValue("baudRate", out var dev) && dev.ValueKind == JsonValueKind.Number && dev.TryGetInt32(out int b1)) return b1;
            if (_osExplicitFields.TryGetValue("baudRate", out var os) && os.ValueKind == JsonValueKind.Number && os.TryGetInt32(out int b2)) return b2;
            return SerialBaudRate;
        }
    }

    public static string CurrentOperatingSystemName =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public string OsBindingLabel => string.Format(Strings.Get("SessionEdit.Serial.Binding.Os"), CurrentOperatingSystemName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegenerateDeviceId))]
    private string _currentDeviceIdDisplay = string.Empty;

    public bool CanRegenerateDeviceId => _syncDeviceId is PersistentDeviceId;

    // 当前作用域交互属性与状态展示
    public string CurrentScopePortName
    {
        get => CurrentSerialScope switch
        {
            SerialScope.OperatingSystem => string.IsNullOrWhiteSpace(SerialOsPort) ? SerialGenericPort : SerialOsPort,
            SerialScope.Device => EffectiveSerialPortName,
            _ => SerialGenericPort
        };
        set
        {
            switch (CurrentSerialScope)
            {
                case SerialScope.OperatingSystem:
                    SerialOsPort = value;
                    break;
                case SerialScope.Device:
                    SerialDevicePort = value;
                    break;
                default:
                    SerialGenericPort = value;
                    break;
            }
            NotifyScopePropertiesChanged();
        }
    }

    public int? CurrentScopeBaudRate
    {
        get => GetScopeIntField("baudRate", SerialBaudRate);
        set
        {
            if (value.HasValue) SetScopeIntField("baudRate", value.Value, v => SerialBaudRate = v);
            else ResetScopeField("baudRate");
            NotifyScopePropertiesChanged();
        }
    }

    public int? CurrentScopeDataBits
    {
        get => GetScopeIntField("dataBits", SerialDataBits);
        set
        {
            if (value.HasValue) SetScopeIntField("dataBits", value.Value, v => SerialDataBits = v);
            else ResetScopeField("dataBits");
            NotifyScopePropertiesChanged();
        }
    }

    public SerialParity? CurrentScopeParity
    {
        get => GetScopeEnumField<SerialParity>("parity", SerialParity);
        set
        {
            if (value.HasValue) SetScopeEnumField("parity", value.Value, v => SerialParity = v);
            else ResetScopeField("parity");
            NotifyScopePropertiesChanged();
        }
    }

    public SerialStopBits? CurrentScopeStopBits
    {
        get => GetScopeEnumField<SerialStopBits>("stopBits", SerialStopBits);
        set
        {
            if (value.HasValue) SetScopeEnumField("stopBits", value.Value, v => SerialStopBits = v);
            else ResetScopeField("stopBits");
            NotifyScopePropertiesChanged();
        }
    }

    public SerialFlowControl? CurrentScopeFlowControl
    {
        get => GetScopeEnumField<SerialFlowControl>("flowControl", SerialFlowControl);
        set
        {
            // 拒绝冲突，不替用户删除通用/OS/设备中的任何显式引脚配置。
            _flowControlRejected = value == SerialFlowControl.RtsCts && GetScopeBoolField("rtsEnable", SerialRtsEnable).HasValue;
            if (_flowControlRejected)
            {
                NotifyScopePropertiesChanged();
                return;
            }
            if (value.HasValue)
            {
                SetScopeEnumField("flowControl", value.Value, v =>
                {
                    SelectedSerialFlowControl = SerialFlowControls.FirstOrDefault(f => f.Mode == v) ?? SerialFlowControls[0];
                });
            }
            else ResetScopeField("flowControl");
            NotifyScopePropertiesChanged();
        }
    }

    public bool? CurrentScopeDtrEnable
    {
        get => GetScopeBoolField("dtrEnable", SerialDtrEnable);
        set
        {
            if (IsGenericScopeSelected)
            {
                SerialDtrEnable = value;
            }
            else if (value.HasValue)
            {
                CurrentDict["dtrEnable"] = JsonSerializer.SerializeToElement(value.Value);
            }
            else
            {
                CurrentDict.Remove("dtrEnable");
            }
            NotifyScopePropertiesChanged();
        }
    }

    public bool? CurrentScopeRtsEnable
    {
        get => GetScopeBoolField("rtsEnable", SerialRtsEnable);
        set
        {
            if (IsSerialRtsConflict && value.HasValue) return;
            if (IsGenericScopeSelected)
            {
                SerialRtsEnable = value;
            }
            else if (value.HasValue)
            {
                CurrentDict["rtsEnable"] = JsonSerializer.SerializeToElement(value.Value);
            }
            else
            {
                CurrentDict.Remove("rtsEnable");
            }
            NotifyScopePropertiesChanged();
        }
    }

    // 继承与显式覆盖状态
    public bool IsPortOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("portName");
    public bool IsBaudRateOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("baudRate");
    public bool IsDataBitsOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("dataBits");
    public bool IsParityOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("parity");
    public bool IsStopBitsOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("stopBits");
    public bool IsFlowControlOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("flowControl");
    public bool IsDtrOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("dtrEnable");
    public bool IsRtsOverriddenInCurrentScope => IsOverrideScopeSelected && CurrentDict.ContainsKey("rtsEnable");

    private Dictionary<string, JsonElement> CurrentDict => CurrentSerialScope switch
    {
        SerialScope.OperatingSystem => _osExplicitFields,
        SerialScope.Device => _deviceExplicitFields,
        _ => _genericStubDict
    };

    private readonly Dictionary<string, JsonElement> _genericStubDict = new(StringComparer.Ordinal);

    private int? GetScopeIntField(string key, int genericDefault)
    {
        if (IsGenericScopeSelected) return genericDefault;
        if (CurrentDict.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int val))
            return val;
        if (IsDeviceScopeSelected && _osExplicitFields.TryGetValue(key, out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out val))
            return val;
        return genericDefault;
    }

    private void SetScopeIntField(string key, int value, Action<int> genericSetter)
    {
        if (IsGenericScopeSelected)
        {
            genericSetter(value);
        }
        else
        {
            CurrentDict[key] = JsonSerializer.SerializeToElement(value);
        }
    }

    private T? GetScopeEnumField<T>(string key, T genericDefault) where T : struct, Enum
    {
        if (IsGenericScopeSelected) return genericDefault;
        if (CurrentDict.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            && Enum.TryParse<T>(el.GetString(), out var val))
            return val;
        if (IsDeviceScopeSelected && _osExplicitFields.TryGetValue(key, out el) && el.ValueKind == JsonValueKind.String
            && Enum.TryParse<T>(el.GetString(), out val))
            return val;
        return genericDefault;
    }

    private void SetScopeEnumField<T>(string key, T value, Action<T> genericSetter) where T : struct, Enum
    {
        if (IsGenericScopeSelected)
        {
            genericSetter(value);
        }
        else
        {
            CurrentDict[key] = JsonSerializer.SerializeToElement(value.ToString());
        }
    }

    private bool? GetScopeBoolField(string key, bool? genericDefault)
    {
        if (IsGenericScopeSelected) return genericDefault;
        if (CurrentDict.TryGetValue(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        if (IsDeviceScopeSelected && _osExplicitFields.TryGetValue(key, out el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        return genericDefault;
    }

    private void ResetScopeField(string key)
    {
        if (!IsGenericScopeSelected)
        {
            CurrentDict.Remove(key);
            if (key == "portName")
            {
                if (IsOsScopeSelected) SerialOsPort = string.Empty;
                else if (IsDeviceScopeSelected) SerialDevicePort = string.Empty;
            }
        }
    }

    [RelayCommand] private void ResetPortOverride() => ResetScopeFieldAndNotify("portName");
    [RelayCommand] private void ResetBaudRateOverride() => ResetScopeFieldAndNotify("baudRate");
    [RelayCommand] private void ResetDataBitsOverride() => ResetScopeFieldAndNotify("dataBits");
    [RelayCommand] private void ResetParityOverride() => ResetScopeFieldAndNotify("parity");
    [RelayCommand] private void ResetStopBitsOverride() => ResetScopeFieldAndNotify("stopBits");
    [RelayCommand] private void ResetFlowControlOverride() => ResetScopeFieldAndNotify("flowControl");
    [RelayCommand] private void ResetDtrOverride() => ResetScopeFieldAndNotify("dtrEnable");
    [RelayCommand] private void ResetRtsOverride() => ResetScopeFieldAndNotify("rtsEnable");

    private void ResetScopeFieldAndNotify(string key)
    {
        ResetScopeField(key);
        NotifyScopePropertiesChanged();
    }

    private void NotifyScopePropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentScopePortName));
        OnPropertyChanged(nameof(CurrentScopeBaudRate));
        OnPropertyChanged(nameof(CurrentScopeDataBits));
        OnPropertyChanged(nameof(CurrentScopeParity));
        OnPropertyChanged(nameof(CurrentScopeStopBits));
        OnPropertyChanged(nameof(CurrentScopeFlowControl));
        OnPropertyChanged(nameof(CurrentScopeDtrEnable));
        OnPropertyChanged(nameof(CurrentScopeRtsEnable));
        OnPropertyChanged(nameof(IsPortOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsBaudRateOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsDataBitsOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsParityOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsStopBitsOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsFlowControlOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsDtrOverriddenInCurrentScope));
        OnPropertyChanged(nameof(IsRtsOverriddenInCurrentScope));
        OnPropertyChanged(nameof(EffectiveScopeFlowControl));
        OnPropertyChanged(nameof(IsSerialRtsConflict));
        OnPropertyChanged(nameof(IsSerialRtsManualAllowed));
        OnPropertyChanged(nameof(IsLowerLevelManualRtsConflict));
        OnPropertyChanged(nameof(EffectiveSerialPortName));
        OnPropertyChanged(nameof(EffectiveSerialBaudRate));
    }

    partial void OnSelectedSerialScopeOptionChanged(SerialScopeOption value)
    {
        _flowControlRejected = false;
        NotifyScopePropertiesChanged();
    }

    partial void OnSerialOsPortChanged(string value)
    {
        UpdateExplicitField(_osExplicitFields, "portName", value);
        NotifyScopePropertiesChanged();
    }

    partial void OnSerialDevicePortChanged(string value)
    {
        UpdateExplicitField(_deviceExplicitFields, "portName", value);
        NotifyScopePropertiesChanged();
    }

    private static void UpdateExplicitField(Dictionary<string, JsonElement> dict, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            dict.Remove(key);
        }
        else
        {
            dict[key] = JsonSerializer.SerializeToElement(value.Trim());
        }
    }

    partial void OnSelectedConnectionProtocolOptionChanged(ConnectionProtocolOption value)
    {
        if (value == null) return;
        SelectedConnectionProtocol = value.Protocol;
    }

    partial void OnSelectedConnectionProtocolChanged(string? oldValue, string newValue)
    {
        if (!_initializingProtocol && oldValue == SessionProtocols.Ssh && newValue == SessionProtocols.Telnet && Port == 22)
        {
            Port = 23;
        }
        else if (!_initializingProtocol && oldValue == SessionProtocols.Telnet && newValue == SessionProtocols.Ssh && Port == 23)
        {
            Port = 22;
        }

        UpdateCategoriesForCurrentProtocol();
    }

    private void InitializeMultiProtocol(
        SessionNode? existing,
        AppSettings global,
        DeviceConfigurationService? deviceConfigService,
        ISyncDeviceId? syncDeviceId)
    {
        _deviceConfigService = deviceConfigService;
        _syncDeviceId = syncDeviceId;

        string currentDeviceId = syncDeviceId?.EnsureCreated() ?? string.Empty;
        CurrentDeviceIdDisplay = currentDeviceId;

        AvailableConnectionProtocols = new ObservableCollection<ConnectionProtocolOption>(SessionBehaviorOptions.ConnectionProtocols);

        string proto = existing?.Protocol ?? SessionProtocols.Ssh;
        var matched = AvailableConnectionProtocols.FirstOrDefault(p => p.Protocol == proto);
        if (matched == null)
        {
            // 发现未知/不受支持的连接类型（如 "vnc", "raw"）
            // 必须保留原值并加入一个显式的不支持选项，严禁静默回退到 SSH！
            string display = string.Format(Strings.Get("SessionEdit.Protocol.Unsupported"), proto);
            matched = new ConnectionProtocolOption(proto, display);
            AvailableConnectionProtocols.Add(matched);
        }

        _initializingProtocol = true;
        try
        {
            SelectedConnectionProtocolOption = matched;
            SelectedConnectionProtocol = proto;
        }
        finally { _initializingProtocol = false; }

        if (existing?.Overrides?.Serial is { } serial)
        {
            SerialGenericPort = serial.PortName ?? string.Empty;
            SerialBaudRate = serial.BaudRate > 0 ? serial.BaudRate : 115200;
            SerialDataBits = serial.DataBits >= 5 && serial.DataBits <= 8 ? serial.DataBits : 8;
            SerialParity = serial.Parity;
            SerialStopBits = serial.StopBits;
            SelectedSerialFlowControl = SerialFlowControls.FirstOrDefault(f => f.Mode == serial.FlowControl) ?? SerialFlowControls[0];
            SerialDtrEnable = serial.DtrEnable;
            SerialRtsEnable = serial.RtsEnable;
        }

        UpdateCategoriesForCurrentProtocol();
    }

    public async Task LoadSerialOverridesAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (_deviceConfigService == null) return;
        var overrides = await _deviceConfigService.GetOverridesAsync(ownerId, "serial", ct);
        string os = CurrentOperatingSystemName;
        string deviceId = _syncDeviceId?.EnsureCreated() ?? string.Empty;

        _osExplicitFields.Clear();
        _deviceExplicitFields.Clear();

        foreach (var record in overrides)
        {
            if (record.Target.OperatingSystem == os)
            {
                foreach (var (k, v) in record.Fields)
                {
                    _osExplicitFields[k] = v.Clone();
                }
            }
            else if (!string.IsNullOrEmpty(deviceId) && record.Target.TargetDeviceId == deviceId)
            {
                foreach (var (k, v) in record.Fields)
                {
                    _deviceExplicitFields[k] = v.Clone();
                }
            }
        }

        if (_osExplicitFields.TryGetValue("portName", out var osPortVal) && osPortVal.ValueKind == JsonValueKind.String)
        {
            SerialOsPort = osPortVal.GetString() ?? string.Empty;
        }
        else
        {
            SerialOsPort = string.Empty;
        }

        if (_deviceExplicitFields.TryGetValue("portName", out var devPortVal) && devPortVal.ValueKind == JsonValueKind.String)
        {
            SerialDevicePort = devPortVal.GetString() ?? string.Empty;
        }
        else
        {
            SerialDevicePort = string.Empty;
        }

        NotifyScopePropertiesChanged();
    }

    private void UpdateCategoriesForCurrentProtocol()
    {
        if (Categories == null || Categories.Count == 0) return;

        bool isSsh = IsSshProtocol;
        var filtered = new ObservableCollection<SettingsCategoryItem>();

        foreach (var cat in _allCategories)
        {
            if (!isSsh && (cat.Page is "FileTransfer" or "Ports"))
            {
                continue;
            }
            filtered.Add(cat);
        }

        Categories = filtered;

        if (SelectedCategory != null && !Categories.Contains(SelectedCategory))
        {
            SelectedCategory = Categories.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void RegenerateDeviceId()
    {
        if (_syncDeviceId is PersistentDeviceId persistent)
        {
            CurrentDeviceIdDisplay = persistent.RecreateAsNewDevice();
            _deviceExplicitFields.Clear();
            SerialDevicePort = string.Empty;
            NotifyScopePropertiesChanged();
        }
        else
        {
            throw new NotSupportedException("当前设备标识提供者不支持持久重建操作。");
        }
    }

    private void ApplyMultiProtocol(SessionNode model)
    {
        model.Protocol = SelectedConnectionProtocol;

        if (IsTelnetProtocol)
        {
            model.Host = Host.Trim();
            model.Port = Port;
        }
        else if (IsSerialProtocol)
        {
            model.Host = string.Empty;
            model.Port = null;

            string genericPort = SerialGenericPort?.Trim() ?? string.Empty;
            model.Overrides.Serial = new SerialOptions
            {
                PortName = genericPort,
                BaudRate = SerialBaudRate,
                DataBits = SerialDataBits,
                Parity = SerialParity,
                StopBits = SerialStopBits,
                FlowControl = SerialFlowControl,
                DtrEnable = SerialDtrEnable,
                RtsEnable = SerialRtsEnable
            };
        }
    }

    public async Task CommitSerialOverridesAsync(Guid ownerId, CancellationToken ct = default)
    {
        if (_deviceConfigService == null || !IsSerialProtocol) return;

        string os = CurrentOperatingSystemName;
        var osTarget = DeviceConfigurationTarget.ForOperatingSystem(os);
        if (_osExplicitFields.Count > 0)
        {
            await _deviceConfigService.SaveOverrideAsync(ownerId, "serial", osTarget, _osExplicitFields, ct);
        }
        else
        {
            await _deviceConfigService.DeleteOverrideAsync(ownerId, "serial", osTarget, ct);
        }

        string deviceId = _syncDeviceId?.EnsureCreated() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var devTarget = DeviceConfigurationTarget.ForDevice(deviceId);
            if (_deviceExplicitFields.Count > 0)
            {
                await _deviceConfigService.SaveOverrideAsync(ownerId, "serial", devTarget, _deviceExplicitFields, ct);
            }
            else
            {
                await _deviceConfigService.DeleteOverrideAsync(ownerId, "serial", devTarget, ct);
            }
        }
    }

    public IReadOnlyDictionary<string, JsonElement> OsExplicitFields => _osExplicitFields;
    public IReadOnlyDictionary<string, JsonElement> DeviceExplicitFields => _deviceExplicitFields;

    public void SetExplicitOverride(DeviceConfigurationTarget target, string key, JsonElement value)
    {
        if (target.OperatingSystem != null)
        {
            _osExplicitFields[key] = value.Clone();
            if (key == "portName" && value.ValueKind == JsonValueKind.String)
                SerialOsPort = value.GetString() ?? string.Empty;
        }
        else if (target.TargetDeviceId != null)
        {
            _deviceExplicitFields[key] = value.Clone();
            if (key == "portName" && value.ValueKind == JsonValueKind.String)
                SerialDevicePort = value.GetString() ?? string.Empty;
        }
        NotifyScopePropertiesChanged();
    }

    public void RemoveExplicitOverride(DeviceConfigurationTarget target, string key)
    {
        if (target.OperatingSystem != null)
        {
            _osExplicitFields.Remove(key);
            if (key == "portName") SerialOsPort = string.Empty;
        }
        else if (target.TargetDeviceId != null)
        {
            _deviceExplicitFields.Remove(key);
            if (key == "portName") SerialDevicePort = string.Empty;
        }
        NotifyScopePropertiesChanged();
    }
}
