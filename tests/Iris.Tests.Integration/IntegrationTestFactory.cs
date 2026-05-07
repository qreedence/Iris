using Iris.Application;
using Iris.Application.Conversations;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Iris.Tests.Integration;

public class IntegrationTestFactory : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:latest")
        .WithDatabase("iris_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    /// <summary>
    /// Creates a raw DbContext for direct database queries and verification.
    /// Used by EventStoreTests and for assertion-side reads in command flow tests.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;

        return new AppDbContext(options);
    }

    /// <summary>
    /// Builds a full DI container with MediatR, EventStore, and DbContext.
    /// Each call returns a new provider — create a scope per command dispatch.
    /// </summary>
    public ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_dbContainer.GetConnectionString()));

        services.AddApplication();
        services.AddScoped<IEventStore, EfEventStore>();

        return services.BuildServiceProvider();
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
