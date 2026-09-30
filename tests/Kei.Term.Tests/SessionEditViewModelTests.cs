using System;
using System.Linq;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SessionEditViewModelTests
{
    [Fact]
    public void SessionEditViewModel_InitializesCategoriesAndTimeoutOptions()
    {
        var settings = new AppSettings { ConnectTimeoutSeconds = 60 };
        var vm = new SessionEditViewModel(null, null, [], settings);

        Assert.Equal(4, vm.Categories.Count);
        Assert.Equal("Connection", vm.Categories[0].Page);
        Assert.NotNull(vm.SelectedCategory);
        Assert.Equal("Connection", vm.SelectedCategory.Page);

        // 默认连接超时为首项（继承全局 60s）
        Assert.NotNull(vm.SelectedConnectTimeout);
        Assert.Null(vm.SelectedConnectTimeout.Value);
    }

    [Fact]
    public void SessionEditViewModel_AppliesTimeoutOverrideToModel()
    {
        var settings = new AppSettings { ConnectTimeoutSeconds = 60 };
        var existing = new SessionNode
        {
            Name = "Server",
            Host = "1.2.3.4",
            Overrides = new SessionOverrides { ConnectTimeoutSeconds = 90 }
        };

        var vm = new SessionEditViewModel(existing, null, [], settings);

        Assert.Equal(90, vm.SelectedConnectTimeout?.Value);

        // 修改为 120s
        vm.SelectedConnectTimeout = vm.ConnectTimeoutOptions.First(o => o.Value == 120);

        var updated = vm.ApplyToModel(existing);
        Assert.Equal(120, updated.Overrides.ConnectTimeoutSeconds);

        // 修改为继承全局
        vm.SelectedConnectTimeout = vm.ConnectTimeoutOptions.First(o => o.Value == null);
        updated = vm.ApplyToModel(existing);
        Assert.Null(updated.Overrides.ConnectTimeoutSeconds);
    }
}
