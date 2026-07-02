using System.Runtime.CompilerServices;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Iris.Tests.Integration;

public class ApiTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestJwtSecret = "test-jwt-secret-with-enough-length-for-hmac-sha256";
    private const string TestJwtIssuer = "Iris.Api";
    private const string TestJwtAudience = "Iris.Client";

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
        mock.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamResponse("Mock AI response", call.ArgAt<CancellationToken>(1)));
        return mock;
    }

    private static async IAsyncEnumerable<StreamedChunk> StreamResponse(
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(content, false, null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk(null, true, new UsageInfo(10, 5, 15));
    }

    public HttpClient CreateAuthenticatedClient(Guid userId, string email = "test@iris.local")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = TestJwtSecret,
                ["Jwt:Issuer"] = TestJwtIssuer,
                ["Jwt:Audience"] = TestJwtAudience,
                ["Jwt:AccessTokenExpirationMinutes"] = "15",
                ["Jwt:RefreshTokenExpirationDays"] = "14",
                ["Google:ClientId"] = "test-google-client-id",
                ["Google:ClientSecret"] = "test-google-client-secret",
                ["IrisSystemPrompt:AppContext"] = "Test app context",
                ["IrisSystemPrompt:Guidelines"] = "Test guidelines"
            });
        });

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

            services.AddSignalR(options => options.EnableDetailedErrors = true);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });
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
