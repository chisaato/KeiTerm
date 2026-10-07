using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Abstractions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class AutomaticReconnectVaultTests
{
    [Theory]
    [InlineData("target-identity")]
    [InlineData("jump-identity")]
    [InlineData("target-proxy")]
    [InlineData("jump-proxy")]
    public async Task LockedVault_StopsAutomaticReconnectWithoutPrompt_WhileManualUnlockStillWorks(string route)
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_reconnect_vault_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "vault.db")}");
            SqliteIdentityRepository identities = new(database);
            await identities.InitializeAsync();
            VaultPasswordMethod method = new();
            Identity identity = new() { Name = "saved-password", Methods = [method] };
            await identities.SaveAsync(identity);
            SqliteProxyRepository proxies = new(database);
            ProxyProfile proxy = new() { Name = "socks", Config = new Socks5ProxyConfig("localhost", 1080, "proxy-user") };
            await proxies.SaveAsync(proxy);
            InternalVaultManager vault = new(database);
            await vault.SetMasterPasswordAsync("master-password");
            await vault.SaveSecretsAsync(identity.Id, new Dictionary<string, SecretPayload>
            {
                [method.Id.ToString()] = new() { Password = "saved-ssh-password" }
            });
            await vault.SetPasswordAsync(proxy.Id, "saved-proxy-password");

            SessionNode destination = new() { Name = "destination", Host = "destination.test", Username = "ops" };
            SessionNode jump = new() { Name = "jump", Host = "jump.test", Username = "ops" };
            switch (route)
            {
                case "target-identity": destination.IdentityId = identity.Id; break;
                case "jump-identity": destination.JumpHostSessionId = jump.Id; jump.IdentityId = identity.Id; break;
                case "target-proxy": destination.ProxyProfileId = proxy.Id; break;
                case "jump-proxy": destination.JumpHostSessionId = jump.Id; jump.ProxyProfileId = proxy.Id; break;
            }

            ScriptedInteraction interaction = new();
            FixedSettingsService settings = new(new AppSettings { AutoReconnectOnDisconnect = true, PreferSystemAgent = true });
            VaultSessionService vaultSession = new(vault, vault, settings, () => interaction);
            AuthMaterialCollector collector = new(identities, settings, vaultSession, () => interaction);
            RecordingFactory factory = new();
            ConnectionOrchestrator orchestrator = new(factory, collector, null, settings, () => interaction,
                action => action(), proxies: proxies, delay: (_, _) => Task.CompletedTask,
                proxySecrets: vault, ensureUnlocked: () => vaultSession.EnsureUnlockedAsync());
            Target target = new();
            Host host = new(destination, jump);
            ConnectionRequest request = new(SessionConfigBuilder.Build(destination, settings.Current), UseIdentity: true, ReuseTarget: target);
            ReconnectFacts facts = new(true, false, false, false, false, 1);

            vault.Lock();
            await orchestrator.ScheduleReconnectAsync(request, host, facts, CancellationToken.None);
            Assert.Equal(0, interaction.MasterPasswordPrompts);
            Assert.Empty(interaction.AuthPromptUsernames);
            Assert.Equal(0, factory.SessionCalls);
            Assert.Equal(0, target.Resets);

            // 已解锁的持久材料仍可用于静默重连。
            await vault.UnlockAsync("master-password", false);
            await orchestrator.ScheduleReconnectAsync(request, host, facts, CancellationToken.None);
            Assert.Equal(1, factory.SessionCalls);
            Assert.Equal(1, target.Connected);
            Assert.Equal(0, interaction.MasterPasswordPrompts);

            // 用户主动重连继续通过原有主密码框解锁。
            vault.Lock();
            interaction.MasterPasswords.Enqueue("master-password");
            await orchestrator.ConnectAsync(request, host);
            Assert.Equal(1, interaction.MasterPasswordPrompts);
            Assert.Equal(2, factory.SessionCalls);
            Assert.Equal(2, target.Connected);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Target : IConnectionTarget
    {
        public bool IsDisposed => false;
        public int Resets { get; private set; }
        public int Connected { get; private set; }
        public void AttachSession(ITerminalSession session) { }
        public Task DetachSessionAsync() => Task.CompletedTask;
        public void MarkConnected() => Connected++;
        public void ReportError(string message) { }
        public void WriteLocalStatus(string message) { }
        public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem) => Task.CompletedTask;
        public Task ResetForReconnectAsync(ResolvedSessionConfig config) { Resets++; return Task.CompletedTask; }
    }

    private sealed class Host(SessionNode destination, SessionNode jump) : IConnectionHost
    {
        public SessionNode? FindSession(Guid id) => id == destination.Id ? destination : id == jump.Id ? jump : null;
        public IConnectionTarget OpenTab(ResolvedSessionConfig config) => throw new InvalidOperationException("Reconnect must reuse its target.");
    }

    private sealed class RecordingFactory : ISshSessionFactory
    {
        public int SessionCalls { get; private set; }
        public Task<ISshSession> CreateSessionAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null, CancellationToken ct = default)
        {
            SessionCalls++;
            return Task.FromResult<ISshSession>(new Session());
        }
        public Task<IRemoteFileSystem> CreateFileSystemAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? sharedSession, SshConnectOptions? options = null, CancellationToken ct = default)
            => Task.FromException<IRemoteFileSystem>(new NotSupportedException());
    }

    private sealed class Session : ISshSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public object? UnderlyingClient => null;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
