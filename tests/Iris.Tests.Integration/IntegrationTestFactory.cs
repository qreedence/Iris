using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Iris.Infrastructure.Persistence;

namespace Iris.Tests.Integration;

public class IntegrationTestFactory : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:latest")
        .WithDatabase("iris_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;

        return new AppDbContext(options);
    }

    public async ValueTask InitializeAsync()
    {
        await _dbContainer.StartAsync();

        using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContainer.StopAsync();
    }
}
