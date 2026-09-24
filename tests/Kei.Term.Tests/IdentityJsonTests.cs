using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Kei.Term.Core.Vault;
using Xunit;

namespace Kei.Term.Tests;

// 多态 JSON：$kind 判别、五方法往返、数组序保持、未知 $kind 抛错
public class IdentityJsonTests
{
    private static List<AuthMethodEntry> SampleMethods() =>
    [
        new VaultPasswordMethod { Id = Guid.NewGuid(), SortOrder = 0 },
        new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SortOrder = 1 },
        new FilePrivateKeyMethod
        {
            Id = Guid.NewGuid(),
            SortOrder = 2,
            KeyFilePath = "~/.ssh/id_ed25519",
            PassphraseMode = PassphrasePersistence.Persistent
        },
        new AgentMethod { Id = Guid.NewGuid(), SortOrder = 3, AgentFingerprint = "SHA256:abc" },
        new InteractiveMethod { Id = Guid.NewGuid(), SortOrder = 4 }
    ];

    [Fact]
    public void Serialize_EmitsAllKindDiscriminators()
    {
        var json = JsonSerializer.Serialize(SampleMethods());

        Assert.Contains("$kind", json);
        Assert.Contains("ssh/vault-password", json);
        Assert.Contains("ssh/vault-key", json);
        Assert.Contains("ssh/file-key", json);
        Assert.Contains("ssh/agent", json);
        Assert.Contains("ssh/interactive", json);
    }

    [Fact]
    public void RoundTrip_PreservesTypesFieldsAndOrder()
    {
        var methods = SampleMethods();

        var json = JsonSerializer.Serialize(methods);
        var back = JsonSerializer.Deserialize<List<AuthMethodEntry>>(json);

        Assert.NotNull(back);
        Assert.Equal(5, back!.Count);
        Assert.IsType<VaultPasswordMethod>(back[0]);
        Assert.IsType<VaultPrivateKeyMethod>(back[1]);
        Assert.IsType<FilePrivateKeyMethod>(back[2]);
        Assert.IsType<AgentMethod>(back[3]);
        Assert.IsType<InteractiveMethod>(back[4]);

        var fileKey = Assert.IsType<FilePrivateKeyMethod>(back[2]);
        Assert.Equal("~/.ssh/id_ed25519", fileKey.KeyFilePath);
        Assert.Equal(PassphrasePersistence.Persistent, fileKey.PassphraseMode);

        var agent = Assert.IsType<AgentMethod>(back[3]);
        Assert.Equal("SHA256:abc", agent.AgentFingerprint);

        // 数组序与各方法 Id/SortOrder 原样保持
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, back.Select(m => m.SortOrder).ToArray());
        Assert.Equal(methods.Select(m => m.Id).ToArray(), back.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Deserialize_UnknownKind_Throws()
    {
        var json = "[{\"$kind\":\"vnc/password\",\"Id\":\"" + Guid.NewGuid() + "\",\"SortOrder\":0,\"Enabled\":true}]";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<List<AuthMethodEntry>>(json));
    }

    [Fact]
    public void DisabledFlag_RoundTrips()
    {
        var methods = new List<AuthMethodEntry> { new AgentMethod { SortOrder = 0, Enabled = false } };

        var back = JsonSerializer.Deserialize<List<AuthMethodEntry>>(JsonSerializer.Serialize(methods));

        Assert.False(back![0].Enabled);
    }
}
