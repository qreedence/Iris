using FluentAssertions;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Domain.AiIntegration;
using Iris.Tests.Integration.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Iris.Tests.Integration.Conversations;

public class ProjectorTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;
    private readonly Guid _userId = Guid.NewGuid();

    public ProjectorTests(IntegrationTestFactory factory)
    {
        _factory = factory;
        _factory.CurrentUser.UserId = _userId;
    }

    private async Task<TResponse> SendCommand<TResponse>(IRequest<TResponse> command)
    {
        using var provider = _factory.CreateServiceProvider();
        return await provider.SendCommandAsync(command, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds a MessageSent event using a fresh DI scope, equivalent to what
    /// SendMessageCommand used to do.
    /// </summary>
    private async Task SendMessage(Guid conversationId, string content, ChatRole role = ChatRole.User)
    {
        using var provider = _factory.CreateServiceProvider();
        await ConversationSeeder.SendMessageAsync(
            provider, conversationId, content, role, ct: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Creates a real persona owned by the fixture's test user, so
    /// CreateConversationCommand's persona-ownership check succeeds.
    /// </summary>
    private async Task<Guid> CreatePersonaAsync()
    {
        using var provider = _factory.CreateServiceProvider();
        var persona = await TestPersonas.CreateAsync(
            provider, _userId, ct: TestContext.Current.CancellationToken);
        return persona.Id;
    }

    // ── §1 ConversationCreated projection ─────────────────────────

    [Fact]
    public async Task CreateConversation_ProjectsConversationReadModel()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync();

        // Act
        await SendCommand(new CreateConversationCommand(conversationId, _userId, personaId, "Projected Chat"));

        // Assert
        await using var db = _factory.CreateDbContext();
        var readModel = await db.ConversationReadModels
            .FirstOrDefaultAsync(c => c.Id == conversationId, TestContext.Current.CancellationToken);

        readModel.Should().NotBeNull();
        readModel!.Title.Should().Be("Projected Chat");
        readModel.MessageCount.Should().Be(0);
        readModel.LastMessageAt.Should().BeNull();
        readModel.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── §2 MessageSent projection — message row ───────────────────

    [Fact]
    public async Task SendMessage_ProjectsConversationMessage()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, personaId, "Chat"));

        // Act
        await SendMessage(conversationId, "Hello, Iris!", ChatRole.User);

        // Assert
        await using var db = _factory.CreateDbContext();
        var message = await db.ConversationMessages
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId, TestContext.Current.CancellationToken);

        message.Should().NotBeNull();
        message!.ConversationId.Should().Be(conversationId);
        message.Content.Should().Be("Hello, Iris!");
        message.Role.Should().Be(ChatRole.User);
        message.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── §3 MessageSent projection — read model update ─────────────

    [Fact]
    public async Task SendMessage_UpdatesConversationReadModel()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, personaId, "Chat"));

        // Act
        await SendMessage(conversationId, "First message", ChatRole.User);

        // Assert
        await using var db = _factory.CreateDbContext();
        var readModel = await db.ConversationReadModels
            .FirstOrDefaultAsync(c => c.Id == conversationId, TestContext.Current.CancellationToken);

        readModel.Should().NotBeNull();
        readModel!.MessageCount.Should().Be(1);
        readModel.LastMessageAt.Should().NotBeNull();
        readModel.LastMessageAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── §4 Multiple messages — accumulation ───────────────────────

    [Fact]
    public async Task SendMessage_MultipleMessages_IncrementsCountAndUpdatesTimestamp()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, personaId, "Chat"));

        // Act
        await SendMessage(conversationId, "First", ChatRole.User);
        await SendMessage(conversationId, "Second", ChatRole.User);
        await SendMessage(conversationId, "Third", ChatRole.User);

        // Assert
        await using var db = _factory.CreateDbContext();
        var readModel = await db.ConversationReadModels
            .FirstOrDefaultAsync(c => c.Id == conversationId, TestContext.Current.CancellationToken);

        readModel.Should().NotBeNull();
        readModel!.MessageCount.Should().Be(3);
        readModel.LastMessageAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── §5 Conversation isolation ─────────────────────────────────

    [Fact]
    public async Task Projectors_DifferentConversations_ReadModelsAreIsolated()
    {
        // Arrange
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var personaA = await CreatePersonaAsync();
        var personaB = await CreatePersonaAsync();

        await SendCommand(new CreateConversationCommand(conversationA, _userId, personaA, "Chat A"));
        await SendCommand(new CreateConversationCommand(conversationB, _userId, personaB, "Chat B"));

        // Act
        await SendMessage(conversationA, "Message A1", ChatRole.User);
        await SendMessage(conversationA, "Message A2", ChatRole.User);
        await SendMessage(conversationB, "Message B1", ChatRole.User);

        // Assert
        await using var db = _factory.CreateDbContext();

        var readModelA = await db.ConversationReadModels.FirstAsync(c => c.Id == conversationA, TestContext.Current.CancellationToken);
        var readModelB = await db.ConversationReadModels.FirstAsync(c => c.Id == conversationB, TestContext.Current.CancellationToken);

        readModelA.MessageCount.Should().Be(2);
        readModelB.MessageCount.Should().Be(1);

        var messagesA = await db.ConversationMessages.Where(m => m.ConversationId == conversationA).ToListAsync(TestContext.Current.CancellationToken);
        var messagesB = await db.ConversationMessages.Where(m => m.ConversationId == conversationB).ToListAsync(TestContext.Current.CancellationToken);

        messagesA.Should().HaveCount(2);
        messagesB.Should().HaveCount(1);
    }

    // ── §6 Message ordering ───────────────────────────────────────

    [Fact]
    public async Task SendMessage_MultipleMessages_ProjectedInOrder()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync();
        await SendCommand(new CreateConversationCommand(conversationId, _userId, personaId, "Chat"));

        // Act
        await SendMessage(conversationId, "First", ChatRole.User);
        await SendMessage(conversationId, "Second", ChatRole.User);
        await SendMessage(conversationId, "Third", ChatRole.User);

        // Assert
        await using var db = _factory.CreateDbContext();
        var messages = await db.ConversationMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(TestContext.Current.CancellationToken);

        messages.Should().HaveCount(3);
        messages[0].Content.Should().Be("First");
        messages[1].Content.Should().Be("Second");
        messages[2].Content.Should().Be("Third");
    }
}
