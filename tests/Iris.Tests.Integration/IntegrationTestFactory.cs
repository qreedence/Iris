using Iris.Application;
using Iris.Application.AiIntegration;
using Iris.Application.Conversations;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Infrastructure;
using Iris.Infrastructure.Persistence;
using Iris.Infrastructure.Personas;
using Iris.Tests.Integration.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration;

public class IntegrationTestFactory : IAsyncLifetime
{
    private readonly TestPostgres _postgres = new();

    /// <summary>
    /// Shared mock IChatProvider. Configure per test via NSubstitute.
    /// Default: returns "Mock AI response" with 10/5 tokens.
    /// </summary>
    public IChatProvider MockChatProvider { get; } = ChatProviderMock.CreateDefault();
    public TestCurrentUserService CurrentUser { get; } = new();

    /// <summary>
    /// Creates a raw DbContext for direct database queries and verification.
    /// Used by EventStoreTests and for assertion-side reads in command flow tests.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.ConnectionString)
            .Options;

        return new AppDbContext(options, CurrentUser);
    }

    /// <summary>
    /// Builds a full DI container with MediatR, EventStore, and DbContext.
    /// Each call returns a new provider — create a scope per command dispatch.
    /// </summary>
    public ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ICurrentUserService>(CurrentUser);
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_postgres.ConnectionString));

        services.AddApplication();
        services.AddSingleton<IGlobalSystemPromptProvider>(
            new TestGlobalSystemPromptProvider("Test app context", "Test guidelines"));
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(EfEventStore).Assembly));
        services.AddScoped<IEventStore, EfEventStore>();
        services.AddScoped<IConversationTurnRequestStore, EfConversationTurnRequestStore>();
        services.AddScoped<IPersonaService, PersonaService>();
        services.AddSingleton<ITurnDoorbell, Iris.Api.Conversations.TurnDoorbell>();
        services.AddSingleton<IActiveTurnRegistry, Iris.Api.Conversations.ActiveTurnRegistry>();
        services.AddSingleton(MockChatProvider);

        return services.BuildServiceProvider();
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAndMigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    private class TestGlobalSystemPromptProvider : IGlobalSystemPromptProvider
    {
        private readonly GlobalSystemPromptSections _sections;

        public TestGlobalSystemPromptProvider(string? appContext, string? guidelines)
        {
            _sections = new GlobalSystemPromptSections(appContext, guidelines);
        }

        public Task<GlobalSystemPromptSections> GetAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_sections);
        }
    }
}
