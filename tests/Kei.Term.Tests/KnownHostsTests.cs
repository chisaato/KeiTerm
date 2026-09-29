using System;
using System.Linq;
using Kei.Term.Core.Security;
using Xunit;

namespace Kei.Term.Tests;

// 主机密钥纯逻辑：指纹格式、判定、策略矩阵、OpenSSH known_hosts 解析与匹配
public class KnownHostsTests
{
    // 以下夹具均由 ssh-keygen 生成，指纹为 `ssh-keygen -lf` 的真实输出
    private const string KeyA = "AAAAC3NzaC1lZDI1NTE5AAAAINNOCjYfYNvN56zScDjq/dDohnNs4wdauZTYtC54QykV";
    private const string KeyAFingerprint = "SHA256:lMz65d+0OmzZYYTRXqePVuIzF8njDm0SKmPbs1feAXY";
    private const string KeyB = "AAAAC3NzaC1lZDI1NTE5AAAAIN/OYsUEL5deLJb9X+lVVMyeUpEed4y/0VWHOn369umv";
    private const string KeyBFingerprint = "SHA256:3DWPXKvapcvReyUUpO5dSxOS4h4pKfFQ0fOK63aUTjE";
    // `ssh-keygen -H` 对 example.com 与 [example.com]:2222 的哈希
    private const string HashedExampleCom = "|1|qmrjyytC429Tb1XlRAA0VQKEX3I=|kC2qKmAL6B1TbF7DPWo+lb+eKzQ=";
    private const string HashedExampleCom2222 = "|1|f1Z3Bxqw3t8aaBqAvjSTEosCbzE=|NjGy8f/J2zd7QReP6HeIYQJmFvs=";

    private static PresentedHostKey Presented(string key, string host = "example.com", int port = 22)
        => new(host, port, "ssh-ed25519", Convert.FromBase64String(key));

    private static KnownHostEntry Entry(string key, KnownHostStatus status = KnownHostStatus.Trusted, string keyType = "ssh-ed25519")
        => new() { Host = "example.com", Port = 22, KeyType = keyType, PublicKeyBase64 = key, Status = status };

    [Fact]
    public void Fingerprint_MatchesOpenSshFormat()
    {
        Assert.Equal(KeyAFingerprint, Presented(KeyA).FingerprintSha256);
        Assert.Equal(KeyBFingerprint, Presented(KeyB).FingerprintSha256);
        Assert.Equal("ssh-ed25519", HostKeyFingerprint.ReadKeyType(Convert.FromBase64String(KeyA)));
        Assert.Null(HostKeyFingerprint.ReadKeyType([0, 0, 0, 99, 1]));
    }

    [Fact]
    public void Evaluate_NoEntries_IsUnknown()
        => Assert.Equal(HostKeyVerdict.Unknown, HostKeyVerifier.Evaluate(Presented(KeyA), []).Verdict);

    [Fact]
    public void Evaluate_SameKeyTrusted_IsTrusted()
        => Assert.Equal(HostKeyVerdict.Trusted, HostKeyVerifier.Evaluate(Presented(KeyA), [Entry(KeyA)]).Verdict);

    [Fact]
    public void Evaluate_DifferentKeySameType_IsChanged()
        => Assert.Equal(HostKeyVerdict.Changed, HostKeyVerifier.Evaluate(Presented(KeyB), [Entry(KeyA)]).Verdict);

    [Fact]
    public void Evaluate_OnlyOtherKeyTypesKnown_IsUnknownNotChanged()
        => Assert.Equal(
            HostKeyVerdict.Unknown,
            HostKeyVerifier.Evaluate(Presented(KeyB), [Entry(KeyA, keyType: "ssh-rsa")]).Verdict);

    [Fact]
    public void Evaluate_RevokedWinsOverTrusted()
        => Assert.Equal(
            HostKeyVerdict.Revoked,
            HostKeyVerifier.Evaluate(Presented(KeyA), [Entry(KeyA), Entry(KeyA, KnownHostStatus.Revoked)]).Verdict);

    [Theory]
    [InlineData(HostKeyVerdict.Trusted, HostKeyPolicy.Strict, HostKeyDecision.AcceptOnce)]
    [InlineData(HostKeyVerdict.Revoked, HostKeyPolicy.Ask, HostKeyDecision.Reject)]
    [InlineData(HostKeyVerdict.Unknown, HostKeyPolicy.Strict, HostKeyDecision.Reject)]
    [InlineData(HostKeyVerdict.Unknown, HostKeyPolicy.AcceptNew, HostKeyDecision.AcceptAndRemember)]
    [InlineData(HostKeyVerdict.Changed, HostKeyPolicy.AcceptNew, HostKeyDecision.Reject)]
    [InlineData(HostKeyVerdict.Changed, HostKeyPolicy.Strict, HostKeyDecision.Reject)]
    public void DecideWithoutPrompt_Matrix(HostKeyVerdict verdict, HostKeyPolicy policy, HostKeyDecision expected)
        => Assert.Equal(expected, HostKeyVerifier.DecideWithoutPrompt(verdict, policy));

    [Theory]
    [InlineData(HostKeyVerdict.Unknown)]
    [InlineData(HostKeyVerdict.Changed)]
    public void DecideWithoutPrompt_AskNeedsConfirmation(HostKeyVerdict verdict)
        => Assert.Null(HostKeyVerifier.DecideWithoutPrompt(verdict, HostKeyPolicy.Ask));

    [Fact]
    public void Matcher_ExactEntryRequiresSamePort()
    {
        var entry = Entry(KeyA);
        Assert.True(KnownHostMatcher.Matches(entry, "EXAMPLE.com", 22));
        Assert.False(KnownHostMatcher.Matches(entry, "example.com", 2222));
    }

    [Fact]
    public void Matcher_HashedEntries_MatchHostAndPort()
    {
        var plain = new KnownHostEntry { Host = HashedExampleCom, Port = 0 };
        var custom = new KnownHostEntry { Host = HashedExampleCom2222, Port = 0 };

        Assert.True(KnownHostMatcher.Matches(plain, "example.com", 22));
        Assert.False(KnownHostMatcher.Matches(plain, "example.org", 22));
        Assert.True(KnownHostMatcher.Matches(custom, "example.com", 2222));
        Assert.False(KnownHostMatcher.Matches(custom, "example.com", 22));
    }

    [Fact]
    public void Matcher_WildcardAndNegation()
    {
        Assert.True(KnownHostMatcher.MatchesPattern("*.corp.example", "db1.corp.example"));
        Assert.True(KnownHostMatcher.MatchesPattern("web?.example", "web1.example"));
        Assert.False(KnownHostMatcher.MatchesPattern("web?.example", "web12.example"));
        Assert.False(KnownHostMatcher.MatchesPattern("*.corp.example,!secret.corp.example", "secret.corp.example"));
        Assert.True(KnownHostMatcher.MatchesPattern("*.corp.example,!secret.corp.example", "api.corp.example"));
        Assert.True(KnownHostMatcher.MatchesPattern("[*.corp.example]:2222", "[db.corp.example]:2222"));
    }

    [Fact]
    public void Parser_ExpandsHostListsAndPorts()
    {
        var content = $"""
            # comment line
            example.com,10.0.0.5 ssh-ed25519 {KeyA} ops@laptop
            [bastion.example]:2222 ssh-ed25519 {KeyB}

            """;

        var result = OpenSshKnownHostsParser.Parse(content);

        Assert.Equal(0, result.SkippedLines);
        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.Host == "example.com" && e.Port == 22 && e.Comment == "ops@laptop");
        Assert.Contains(result.Entries, e => e.Host == "10.0.0.5" && e.Port == 22);
        var bastion = Assert.Single(result.Entries, e => e.Host == "bastion.example");
        Assert.Equal(2222, bastion.Port);
        Assert.Equal(KeyBFingerprint, bastion.FingerprintSha256);
        Assert.All(result.Entries, e => Assert.Equal(KnownHostSource.Imported, e.Source));
    }

    [Fact]
    public void Parser_KeepsHashedAndWildcardAsPatterns_AndHandlesMarkers()
    {
        var content = $"""
            {HashedExampleCom} ssh-ed25519 {KeyA}
            *.corp.example ssh-ed25519 {KeyA}
            @revoked old.example ssh-ed25519 {KeyB}
            @cert-authority *.example ssh-ed25519 {KeyA}
            """;

        var result = OpenSshKnownHostsParser.Parse(content);

        Assert.Equal(1, result.SkippedLines); // @cert-authority 暂不支持
        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.Host == HashedExampleCom && e.IsPattern);
        Assert.Contains(result.Entries, e => e.Host == "*.corp.example" && e.IsPattern);
        var revoked = Assert.Single(result.Entries, e => e.Status == KnownHostStatus.Revoked);
        Assert.Equal("old.example", revoked.Host);
    }

    [Fact]
    public void Parser_RejectsMalformedAndMismatchedLines()
    {
        var content = $"""
            example.com ssh-ed25519 not-base64!!
            example.com ssh-rsa {KeyA}
            example.com ssh-ed25519
            """;

        var result = OpenSshKnownHostsParser.Parse(content);

        Assert.Empty(result.Entries);
        Assert.Equal(3, result.SkippedLines);
    }
}
