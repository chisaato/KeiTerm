using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public class ComposeModeTests
{
    [Fact]
    public async Task ComposeMode_CommandAndToggle_KeepBothModesConsistent()
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = new(database);
        MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database), vault, vault,
            new FixedSettingsService(), new SshSessionFactory());
        try
        {
            model.SetComposeModeCommand.Execute(ComposeMode.MultiLine);
            Assert.Equal(ComposeMode.MultiLine, model.ComposeMode);
            Assert.True(model.IsMultiLineCompose);
            Assert.False(model.IsSingleLineCompose);

            model.IsSingleLineCompose = true;
            Assert.Equal(ComposeMode.SingleLine, model.ComposeMode);
            Assert.True(model.IsSingleLineCompose);
            Assert.False(model.IsMultiLineCompose);
        }
        finally
        {
            await model.DisposeAsync();
        }
    }
}
