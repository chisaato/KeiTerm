using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SerialPortDiscoveryViewTests
{
    [Fact]
    public Task SerialPortDiscoveryView_ControlsAndBindings_ContractVerified() => HeadlessAvalonia.RunAsync(async () =>
    {
        var vm = new SessionEditViewModel(
            existing: null,
            parentId: null,
            availableIdentities: [],
            globalSettings: new AppSettings());

        // 切换为 Serial 协议
        vm.SelectedConnectionProtocol = SessionProtocols.Serial;

        // 模拟提供串口枚举列表
        vm.SerialPortNames.Add("/dev/ttyUSB0");
        vm.SerialPortNames.Add("/dev/ttyUSB1");

        var window = new SessionEditWindow(vm);
        window.Show();
        HeadlessAvalonia.Pump();

        // 1. 验证 AutoCompleteBox 控件契约：ItemsSource 可选，且手填端口保留
        var autoBox = window.FindControl<AutoCompleteBox>("SerialScopePortAutoCompleteBox");
        Assert.NotNull(autoBox);
        Assert.NotNull(autoBox.ItemsSource);
        var names = Assert.IsAssignableFrom<ObservableCollection<string>>(autoBox.ItemsSource);
        Assert.Contains("/dev/ttyUSB0", names);
        Assert.Contains("/dev/ttyUSB1", names);

        // 手动输入任意自定义端口，验证绑定与手输保留
        autoBox.Text = "/dev/ttyCustom42";
        HeadlessAvalonia.Pump();
        Assert.Equal("/dev/ttyCustom42", vm.CurrentScopePortName);
        Assert.Equal("/dev/ttyCustom42", vm.SerialGenericPort);

        // 2. 验证刷新按钮与命令绑定契约
        var refreshBtn = window.FindControl<Button>("RefreshSerialPortsButton");
        Assert.NotNull(refreshBtn);
        Assert.NotNull(refreshBtn.Command);
        Assert.True(refreshBtn.Command.CanExecute(null));

        // 3. 验证发现错误提示面板的显隐契约
        var errorBorder = window.FindControl<Border>("SerialPortDiscoveryErrorBorder");
        Assert.NotNull(errorBorder);

        // 初始无错误：隐藏
        vm.SerialPortDiscoveryError = string.Empty;
        HeadlessAvalonia.Pump();
        Assert.False(errorBorder.IsVisible);

        // 发生错误：可见
        vm.SerialPortDiscoveryError = "未找到串口发现服务";
        HeadlessAvalonia.Pump();
        Assert.True(errorBorder.IsVisible);

        // 错误清空：再次隐藏
        vm.SerialPortDiscoveryError = string.Empty;
        HeadlessAvalonia.Pump();
        Assert.False(errorBorder.IsVisible);

        window.Close();
        HeadlessAvalonia.Pump();
        await Task.CompletedTask;
    });
}
