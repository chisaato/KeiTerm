using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Vault;

namespace Kei.Term.Tests;

public partial class ConnectionOrchestratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityInteractive_IsCollectedInOrderWithoutOpeningAuthDialog(bool jump)
    {
        var password = new VaultPasswordMethod { SortOrder = 0 };
        var identity = new Identity { Name = "ordered", Username = "ops", Methods = [new InteractiveMethod { SortOrder = 1 }, password] };
        var identities = new InMemoryIdentities();
        await identities.SaveAsync(identity);
        var store = new PlainVault();
        await store.SaveSecretsAsync(identity.Id, new Dictionary<string, SecretPayload>
        {
            [password.Id.ToString()] = new() { Password = "saved" }
        });
        var vault = new VaultSessionService(store, store, _settings, () => _ui);
        var collector = new AuthMaterialCollector(identities, _settings, vault, () => _ui);
        IReadOnlyList<MaterializedAuthMethod> materials;
        if (jump)
        {
            var hop = new Kei.Term.Core.Models.SessionNode { Name = "jump", Host = "jump.test", IdentityId = identity.Id };
            var hops = await collector.CollectJumpHopsAsync(Config(jump: hop.Id), id => id == hop.Id ? hop : null);
            materials = Assert.Single(hops).Methods;
        }
        else
        {
            var auth = await collector.CollectTargetAsync(new ConnectionRequest(Config() with { IdentityId = identity.Id }, UseIdentity: true));
            Assert.NotNull(auth);
            materials = auth.Materials;
        }
        Assert.Equal(new[] { AuthMaterialKind.Password, AuthMaterialKind.Interactive }, materials.Select(m => m.Kind));
        Assert.Equal("saved", materials[0].Secret!.Password);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Empty(_ui.KeyboardInteractivePrompts);
    }
}
