using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Xunit;

namespace Kei.Term.Tests;

public class SqliteIdentityRepositoryTests
{
    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"keiterm_identity_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Crud_RoundTripsIdentityAndMethods()
    {
        var dbPath = NewDbPath();
        try
        {
            var repo = new SqliteIdentityRepository($"Data Source={dbPath}");
            await repo.InitializeAsync();

            var identity = new Identity
            {
                Name = "Prod",
                Description = "生产环境",
                Username = "ops",
                Methods =
                [
                    new AgentMethod { SortOrder = 0, AgentFingerprint = "SHA256:xyz" },
                    new VaultPasswordMethod { SortOrder = 1 }
                ]
            };

            await repo.SaveAsync(identity);

            var loaded = await repo.GetByIdAsync(identity.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Prod", loaded!.Name);
            Assert.Equal("生产环境", loaded.Description);
            Assert.Equal("ops", loaded.Username);
            Assert.Equal(2, loaded.Methods.Count);
            Assert.IsType<AgentMethod>(loaded.Methods[0]);
            Assert.IsType<VaultPasswordMethod>(loaded.Methods[1]);

            Assert.Single(await repo.GetAllAsync());

            await repo.DeleteAsync(identity.Id);
            Assert.Null(await repo.GetByIdAsync(identity.Id));
            Assert.Empty(await repo.GetAllAsync());
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task Save_UpdatesExistingRow()
    {
        var dbPath = NewDbPath();
        try
        {
            var repo = new SqliteIdentityRepository($"Data Source={dbPath}");
            await repo.InitializeAsync();

            var identity = new Identity { Name = "Before" };
            await repo.SaveAsync(identity);

            identity.Name = "After";
            identity.Methods.Add(new InteractiveMethod());
            await repo.SaveAsync(identity);

            var loaded = await repo.GetByIdAsync(identity.Id);
            Assert.Equal("After", loaded!.Name);
            Assert.IsType<InteractiveMethod>(Assert.Single(loaded.Methods));
            Assert.Single(await repo.GetAllAsync());
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
