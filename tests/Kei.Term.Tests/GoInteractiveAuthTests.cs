using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Transport.Go;

namespace Kei.Term.Tests;

public class GoInteractiveAuthTests
{
    [Fact]
    public async Task ExplicitInteractive_IsRejectedBeforeSendingIncompleteCredentials()
    {
        var factory = new GoSshSessionFactory(_ => throw new InvalidOperationException("Must not open IPC"), new UnusedVerifier());
        var config = SessionConfigBuilder.Build(new SessionNode { Host = "example.test" }, new AppSettings());
        await using var session = await factory.CreateSessionAsync(config,
            [new(AuthMaterialKind.Password, new SecretPayload { Password = "pw" }), new(AuthMaterialKind.Interactive, null)]);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ConnectAsync());
    }

    private sealed class UnusedVerifier : IHostKeyVerifier
    {
        public Task<IReadOnlyList<string>> GetKnownKeyTypesAsync(string host, int port, CancellationToken ct = default)
            => throw new InvalidOperationException("Must not probe host");
        public Task<HostKeyCheckOutcome> VerifyAsync(PresentedHostKey presented, CancellationToken ct = default)
            => throw new InvalidOperationException("Must not probe host");
    }
}
