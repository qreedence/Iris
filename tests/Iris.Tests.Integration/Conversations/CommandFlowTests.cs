using FluentAssertions;
using Iris.Application.Conversations;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Conversations.Commands.SendMessage;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
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
    /// Loads the event stream for a given conversation via a fresh DbContext.
    /// Used for assertion-side verification.
    /// </summary>
    private async Task<IReadOnlyList<ConversationEvent>> LoadStream(Guid conversationId)
    {
        await using var db = _factory.CreateDbContext();
        var store = new EfEventStore(db);
        return await store.LoadStreamAsync(conversationId, TestContext.Current.CancellationToken);
    }

    // ── §1 CreateConversation end-to-end ──────────────────────────

    [Fact]
    public async Task CreateConversation_ValidCommand_PersistsConversationCreatedEvent()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();
        var command = new CreateConversationCommand(conversationId, personaId, "My First Chat");

        // Act
        var result = await SendCommand(command);

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
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        var sendCommand = new SendMessageCommand(conversationId, "Hello, Iris!", ChatRole.User);

        // Act
        await SendCommand(sendCommand);

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
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act
        await SendCommand(new SendMessageCommand(conversationId, "First message", ChatRole.User));
        await SendCommand(new SendMessageCommand(conversationId, "Second message", ChatRole.User));
        await SendCommand(new SendMessageCommand(conversationId, "Third message", ChatRole.User));

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

        await SendCommand(new CreateConversationCommand(conversationA, Guid.NewGuid(), "Chat A"));
        await SendCommand(new CreateConversationCommand(conversationB, Guid.NewGuid(), "Chat B"));

        // Act
        await SendCommand(new SendMessageCommand(conversationA, "Message for A", ChatRole.User));
        await SendCommand(new SendMessageCommand(conversationB, "Message for B", ChatRole.User));

        // Assert
        var streamA = await LoadStream(conversationA);
        var streamB = await LoadStream(conversationB);

        streamA.Should().HaveCount(2);
        streamA.Should().AllSatisfy(e => e.ConversationId.Should().Be(conversationA));

        streamB.Should().HaveCount(2);
        streamB.Should().AllSatisfy(e => e.ConversationId.Should().Be(conversationB));
    }

    // ── §5 Validation through MediatR ─────────────────────────────

    [Fact]
    public async Task SendMessage_ToNonExistentConversation_ThrowsNotFoundException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var command = new SendMessageCommand(nonExistentId, "Hello?", ChatRole.User);

        // Act
        var act = () => SendCommand(command);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*does not exist*");
    }
}
