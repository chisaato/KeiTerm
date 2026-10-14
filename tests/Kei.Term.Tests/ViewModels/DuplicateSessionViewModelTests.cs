using System;
using System.Collections.Generic;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Xunit;

namespace Kei.Term.Tests.ViewModels;

public class DuplicateSessionViewModelTests
{
    [Fact]
    public void GenerateDefaultName_WhenNoConflict_AppendsOne()
    {
        string baseName = "router-gdgz";
        var existing = new List<string> { "router-gdgz", "debian-build" };

        string result = DuplicateSessionViewModel.GenerateDefaultName(baseName, existing);

        Assert.Equal("router-gdgz (1)", result);
    }

    [Fact]
    public void GenerateDefaultName_WhenOneExists_IncrementsToTwo()
    {
        string baseName = "router-gdgz";
        var existing = new List<string> { "router-gdgz", "router-gdgz (1)" };

        string result = DuplicateSessionViewModel.GenerateDefaultName(baseName, existing);

        Assert.Equal("router-gdgz (2)", result);
    }

    [Fact]
    public void GenerateDefaultName_WhenSourceAlreadyHasSuffix_ReplacesOrIncrements()
    {
        string baseName = "router-gdgz (1)";
        var existing = new List<string> { "router-gdgz", "router-gdgz (1)" };

        string result = DuplicateSessionViewModel.GenerateDefaultName(baseName, existing);

        Assert.Equal("router-gdgz (2)", result);
    }

    [Fact]
    public void CanConfirm_ValidatesNameHostAndPort()
    {
        var session = new SessionNode
        {
            Id = Guid.NewGuid(),
            Name = "srv",
            Host = "192.168.1.1",
            Port = 22
        };

        var vm = new DuplicateSessionViewModel(session, []);

        Assert.True(vm.CanConfirm);

        vm.Name = "";
        Assert.False(vm.CanConfirm);

        vm.Name = "srv (1)";
        vm.Hostname = "  ";
        Assert.False(vm.CanConfirm);

        vm.Hostname = "10.0.0.1";
        vm.Port = 0;
        Assert.False(vm.CanConfirm);

        vm.Port = 70000;
        Assert.False(vm.CanConfirm);

        vm.Port = 2222;
        Assert.True(vm.CanConfirm);
    }
}
