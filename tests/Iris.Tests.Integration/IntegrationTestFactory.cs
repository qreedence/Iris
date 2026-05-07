using Iris.Application;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations;
using Iris.Infrastructure;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
    /// Shared mock IChatProvider. Configure per test via NSubstitute.
    /// Default: returns "Mock AI response" with 10/5 tokens.
    /// </summary>
    public IChatProvider MockChatProvider { get; } = CreateDefaultMockChatProvider();

    private static IChatProvider CreateDefaultMockChatProvider()
    {
        var mock = Substitute.For<IChatProvider>();
        mock.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Mock AI response", new UsageInfo(10, 5, 15)));
        return mock;
    }

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
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(EfEventStore).Assembly));
        services.AddScoped<IEventStore, EfEventStore>();
        services.AddSingleton(MockChatProvider);

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
