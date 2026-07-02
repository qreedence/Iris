using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Exceptions;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using Iris.Tests.Integration.Helpers;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Tests.Integration.Conversations;

public class CommandFlowTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public CommandFlowTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Dispatches a command through MediatR using a fresh DI scope.
    /// Each call simulates a separate HTTP request.
    /// </summary>
    private async Task<TResponse> SendCommand<TResponse>(IRequest<TResponse> command)
    {
        using var provider = _factory.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Dispatches a command as the given user — sets the ambient current-user
    /// (used by AppDbContext's Persona query filter) before sending.
    /// </summary>
    private async Task<TResponse> SendCommandAs<TResponse>(Guid userId, IRequest<TResponse> command)
    {
        _factory.CurrentUser.UserId = userId;
        return await SendCommand(command);
    }

    /// <summary>
    /// Loads the event stream for a given conversation via a fresh DbContext.
    /// Used for assertion-side verification.
    /// </summary>
    private async Task<IReadOnlyList<ConversationEvent>> LoadStream(Guid conversationId)
    {
        await using var db = _factory.CreateDbContext();
        var store = new EfEventStore(db);
        return await store.LoadStreamAsync(conversationId, TestContext.Current.CancellationToken);
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
    /// Creates a real persona so CreateConversationCommand's persona-ownership
    /// check (in CreateConversationHandler) succeeds.
    /// </summary>
    private async Task<Guid> CreatePersonaAsync(Guid userId)
    {
        _factory.CurrentUser.UserId = userId;

        using var provider = _factory.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var personaService = scope.ServiceProvider.GetRequiredService<IPersonaService>();
        var persona = await personaService.CreateAsync(
            userId, new CreatePersonaRequest("Iris"), TestContext.Current.CancellationToken);
        return persona.Id;
    }

    // ── §1 CreateConversation end-to-end ──────────────────────────

    [Fact]
    public async Task CreateConversation_ValidCommand_PersistsConversationCreatedEvent()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync(userId);
        var command = new CreateConversationCommand(conversationId, userId, personaId, "My First Chat");

        // Act
        var result = await SendCommandAs(userId, command);

        // Assert
        result.Should().Be(conversationId);

        var stream = await LoadStream(conversationId);
        stream.Should().HaveCount(1);

        var created = stream[0].Should().BeOfType<ConversationCreated>().Subject;
        created.ConversationId.Should().Be(conversationId);
        created.PersonaId.Should().Be(personaId);
        created.Title.Should().Be("My First Chat");
    }

    // ── §2 SendMessage end-to-end ─────────────────────────────────

    [Fact]
    public async Task SendMessage_AfterConversationCreated_PersistsMessageSentEvent()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync(userId);
        await SendCommandAs(userId, new CreateConversationCommand(conversationId, userId, personaId, "Chat"));

        // Act
        await SendMessage(conversationId, "Hello, Iris!", ChatRole.User);

        // Assert
        var stream = await LoadStream(conversationId);
        stream.Should().HaveCount(2);

        stream[0].Should().BeOfType<ConversationCreated>();

        var message = stream[1].Should().BeOfType<MessageSent>().Subject;
        message.ConversationId.Should().Be(conversationId);
        message.Content.Should().Be("Hello, Iris!");
        message.Role.Should().Be(ChatRole.User);
    }

    // ── §3 Multiple messages ──────────────────────────────────────

    [Fact]
    public async Task SendMessage_MultipleMessages_AllPersistedInOrder()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync(userId);
        await SendCommandAs(userId, new CreateConversationCommand(conversationId, userId, personaId, "Chat"));

        // Act
        await SendMessage(conversationId, "First message", ChatRole.User);
        await SendMessage(conversationId, "Second message", ChatRole.User);
        await SendMessage(conversationId, "Third message", ChatRole.User);

        // Assert
        var stream = await LoadStream(conversationId);
        stream.Should().HaveCount(4); // 1 created + 3 messages

        stream[0].Should().BeOfType<ConversationCreated>();

        var messages = stream.Skip(1).Cast<MessageSent>().ToList();
        messages[0].Content.Should().Be("First message");
        messages[1].Content.Should().Be("Second message");
        messages[2].Content.Should().Be("Third message");
    }

    // ── §4 Conversation isolation ─────────────────────────────────

    [Fact]
    public async Task Commands_DifferentConversations_EventsDoNotLeak()
    {
        // Arrange
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var personaA = await CreatePersonaAsync(userA);
        var personaB = await CreatePersonaAsync(userB);

        await SendCommandAs(userA, new CreateConversationCommand(conversationA, userA, personaA, "Chat A"));
        await SendCommandAs(userB, new CreateConversationCommand(conversationB, userB, personaB, "Chat B"));

        // Act
        await SendMessage(conversationA, "Message for A", ChatRole.User);
        await SendMessage(conversationB, "Message for B", ChatRole.User);

        // Assert
        var streamA = await LoadStream(conversationA);
        var streamB = await LoadStream(conversationB);

        streamA.Should().HaveCount(2);
        streamA.Should().AllSatisfy(e => e.ConversationId.Should().Be(conversationA));

        streamB.Should().HaveCount(2);
        streamB.Should().AllSatisfy(e => e.ConversationId.Should().Be(conversationB));
    }

    // ── §5 Duplicate creation guard ─────────────────────────────────

    [Fact]
    public async Task CreateConversation_DuplicateId_ThrowsValidationException()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var personaId = await CreatePersonaAsync(userId);
        await SendCommandAs(userId, new CreateConversationCommand(conversationId, userId, personaId, "First"));

        // Act — same ID again
        var act = () => SendCommandAs(userId, new CreateConversationCommand(conversationId, userId, personaId, "Duplicate"));

        // Assert
        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*already exists*");

        // Verify only the original event is in the stream
        var stream = await LoadStream(conversationId);
        stream.Should().HaveCount(1);
    }

}
