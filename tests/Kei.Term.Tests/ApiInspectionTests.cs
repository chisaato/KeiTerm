using System;
using System.Linq;
using System.Reflection;
using Renci.SshNet;
using Xunit;
using Xunit.Abstractions;

namespace Kei.Term.Tests;

public class ApiInspectionTests
{
    private readonly ITestOutputHelper _output;

    public ApiInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectShellStreamApis()
    {
        var streamType = typeof(ShellStream);
        var methods = streamType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                               .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                               .Distinct()
                               .ToList();
        
        foreach (var m in methods)
        {
            _output.WriteLine(m);
        }
        Assert.NotEmpty(methods);
    }
}
