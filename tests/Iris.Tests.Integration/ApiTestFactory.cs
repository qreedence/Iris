using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Iris.Tests.Integration;

public class ApiTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove the real DbContext registration
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));

            if (descriptor != null)
                services.Remove(descriptor);

            // Add DbContext pointing to Testcontainers PostgreSQL
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));

            // Replace real IChatProvider with mock
            var chatProviderDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IChatProvider));
            if (chatProviderDescriptor != null)
                services.Remove(chatProviderDescriptor);

            services.AddSingleton(MockChatProvider);
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _dbContainer.StartAsync();

        // Run migrations
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await _dbContainer.StopAsync();
        await base.DisposeAsync();
    }
}
