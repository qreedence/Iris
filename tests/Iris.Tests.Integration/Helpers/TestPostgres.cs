using Iris.Infrastructure.Persistence;
using Iris.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Iris.Tests.Integration.Helpers;

/// <summary>
/// Shared Postgres Testcontainers bootstrap used by both <see cref="ApiTestFactory"/>
/// and <see cref="IntegrationTestFactory"/>. Owns container lifecycle (start/stop) and
/// migration application, so the two factories can't drift on how the database is
/// stood up.
/// </summary>
public sealed class TestPostgres : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:latest")
        .WithDatabase("iris_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task StartAndMigrateAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var db = new AppDbContext(options, new TestCurrentUserService());
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.StopAsync();
    }
}
