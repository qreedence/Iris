using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Models;
using Iris.Application.Conversations.Commands.Chat;
using Iris.Application.Conversations.Commands.CreateConversation;
using Iris.Application.Exceptions;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Iris.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Iris.Tests.Integration.Conversations;

public class ChatFlowTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public ChatFlowTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    private async Task<TResponse> SendCommand<TResponse>(IRequest<TResponse> command)
    {
        using var provider = _factory.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(command, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<ConversationEvent>> LoadStream(Guid conversationId)
    {
        await using var db = _factory.CreateDbContext();
        var store = new EfEventStore(db);
        return await store.LoadStreamAsync(conversationId, TestContext.Current.CancellationToken);
    }

    // ── §1 Complete turn — events ─────────────────────────────────

    [Fact]
    public async Task Chat_CompleteTurn_PersistsAllEvents()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act — single call handles user message + AI response
        await SendCommand(new ChatCommand(conversationId, "Hello!", "test/model"));

        // Assert — ConversationCreated + MessageSent + AssistantResponseCompleted + TurnCompleted
        var stream = await LoadStream(conversationId);

        stream.Should().HaveCount(4);
        stream[0].Should().BeOfType<ConversationCreated>();
        stream[1].Should().BeOfType<MessageSent>();
        stream[2].Should().BeOfType<AssistantResponseCompleted>();
        stream[3].Should().BeOfType<TurnCompleted>();
    }

    // ── §2 Complete turn — read model ─────────────────────────────

    [Fact]
    public async Task Chat_CompleteTurn_AssistantResponseInMessages()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act
        await SendCommand(new ChatCommand(conversationId, "Hi there", "test/model"));

        // Assert
        await using var db = _factory.CreateDbContext();
        var assistantMessages = await db.ConversationMessages
            .Where(m => m.ConversationId == conversationId && m.Role == ChatRole.Assistant)
            .ToListAsync(TestContext.Current.CancellationToken);

        assistantMessages.Should().HaveCount(1);
        assistantMessages[0].Content.Should().NotBeNullOrEmpty();
        assistantMessages[0].Role.Should().Be(ChatRole.Assistant);
    }

    // ── §3 Multi-turn — AI receives history ───────────────────────

    [Fact]
    public async Task Chat_SecondTurn_AiReceivesFullHistory()
    {
        // Arrange — configure mock to capture the request on second call
        ChatRequest? capturedRequest = null;
        _factory.MockChatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Turn 1 response", new UsageInfo(10, 5, 15)),
                     new ChatResponse("Turn 2 response", new UsageInfo(20, 10, 30)))
            .AndDoes(info => capturedRequest = info.Arg<ChatRequest>());

        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Turn 1
        await SendCommand(new ChatCommand(conversationId, "First question", "test/model"));

        // Act — Turn 2
        await SendCommand(new ChatCommand(conversationId, "Follow-up", "test/model"));

        // Assert — second call should have full history: user1, assistant1, user2
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Messages.Should().HaveCount(3);
        capturedRequest.Messages[0].Role.Should().Be(ChatRole.User);
        capturedRequest.Messages[0].Content.Should().Be("First question");
        capturedRequest.Messages[1].Role.Should().Be(ChatRole.Assistant);
        capturedRequest.Messages[1].Content.Should().Be("Turn 1 response");
        capturedRequest.Messages[2].Role.Should().Be(ChatRole.User);
        capturedRequest.Messages[2].Content.Should().Be("Follow-up");
    }

    // ── §4 Message count ──────────────────────────────────────────

    [Fact]
    public async Task Chat_CompleteTurn_UpdatesConversationReadModel()
    {
        // Arrange
        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act
        await SendCommand(new ChatCommand(conversationId, "Hello", "test/model"));

        // Assert — 1 user message + 1 assistant response = 2
        await using var db = _factory.CreateDbContext();
        var readModel = await db.ConversationReadModels.FirstAsync(c => c.Id == conversationId, TestContext.Current.CancellationToken);

        readModel.MessageCount.Should().Be(2);
        readModel.LastMessageAt.Should().NotBeNull();
    }

    // ── §5 Non-existent conversation ──────────────────────────────

    [Fact]
    public async Task Chat_NonExistentConversation_ThrowsNotFoundException()
    {
        // Act
        var act = () => SendCommand(new ChatCommand(Guid.NewGuid(), "Hello", "test/model"));

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── §6 Token tracking ─────────────────────────────────────────

    [Fact]
    public async Task Chat_CompleteTurn_TurnCompletedHasUsageInfo()
    {
        // Arrange
        _factory.MockChatProvider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse("Response", new UsageInfo(150, 42, 192)));

        var conversationId = Guid.NewGuid();
        await SendCommand(new CreateConversationCommand(conversationId, Guid.NewGuid(), "Chat"));

        // Act
        await SendCommand(new ChatCommand(conversationId, "Count my tokens", "test/model"));

        // Assert
        var stream = await LoadStream(conversationId);
        var turnCompleted = stream.OfType<TurnCompleted>().First();

        turnCompleted.InputTokens.Should().Be(150);
        turnCompleted.OutputTokens.Should().Be(42);
    }
}
