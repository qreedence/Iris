using System.Runtime.CompilerServices;
using FluentAssertions;
using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Conversations;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Iris.Domain.Conversations.Content;

namespace Iris.Tests.Unit.Conversations;

public class ChatStreamOrchestratorTests
{
    private readonly IConversationTurnPreparer _turnPreparer = Substitute.For<IConversationTurnPreparer>();
    private readonly IChatProvider _chatProvider = Substitute.For<IChatProvider>();
    private readonly IChatStreamNotifier _notifier = Substitute.For<IChatStreamNotifier>();
    private readonly IConversationEventRecorder _eventRecorder = Substitute.For<IConversationEventRecorder>();
    private readonly IToolRegistry _toolRegistry = Substitute.For<IToolRegistry>();
    private readonly IToolExecutionRecorder _toolExecutionRecorder = Substitute.For<IToolExecutionRecorder>();
    private readonly IActiveTurnRegistry _activeTurns = Substitute.For<IActiveTurnRegistry>();

    private ChatStreamOrchestrator CreateSut()
    {
        _eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>(Array.Empty<RecordedEvent>()));

        _eventRecorder.ClearReceivedCalls();

        _toolExecutionRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<ToolCall>(),
                Arg.Any<ToolResult>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var toolCall = call.Arg<ToolCall>();
                var result = call.Arg<ToolResult>();
                return new ToolExecuted(
                    call.ArgAt<Guid>(0),
                    call.ArgAt<Guid>(1),
                    toolCall.Id,
                    toolCall.FunctionName,
                    Guid.NewGuid(),
                    result.Status,
                    call.ArgAt<long>(4));
            });

        return new(
            _turnPreparer,
            _chatProvider,
            _notifier,
            _eventRecorder,
            _toolRegistry,
            _toolExecutionRecorder,
            _activeTurns,
            TimeProvider.System,
            NullLogger<ChatStreamOrchestrator>.Instance);
    }

    [Fact]
    public async Task StreamAsync_Success_StreamsChunksAndRecordsCompletionEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var chatRequest = new ChatRequest(
            "test/model",
            [
                new ChatMessage(ChatRole.User, MessageContentBlocks.Text("First user message")),
                new ChatMessage(ChatRole.Assistant, MessageContentBlocks.Text("First assistant response")),
                new ChatMessage(ChatRole.User, MessageContentBlocks.Text("Second user message"))
            ],
            "You are Iris.",
            null);
        SetupPreparedTurn(conversationId, chatRequest);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("Hello", false, null),
                new StreamedChunk(" there", false, null),
                new StreamedChunk(null, true, new UsageInfo(12, 5, 17))
            ], call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        _chatProvider.Received(1).StreamAsync(chatRequest, Arg.Any<CancellationToken>());

        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.ConversationId == conversationId &&
                chunk.BlockType == ContentBlockType.Text &&
                chunk.BlockIndex == 0 &&
                chunk.Content == "Hello"),
            Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.ConversationId == conversationId &&
                chunk.BlockType == ContentBlockType.Text &&
                chunk.BlockIndex == 0 &&
                chunk.Content == " there"),
            Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());

        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCompletionEvents(events)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_MixedThinkingAndText_RecordsOrderedContentBlocks()
    {
        // Arrange
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = "reasoning.text",
            ["text"] = "Let me think",
            ["signature"] = "sig-123",
            ["id"] = "reasoning-text-1",
            ["format"] = "anthropic-claude-v1",
            ["index"] = 0
        };
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk(
                    "Let me think",
                    false,
                    null,
                    ContentBlockType.Thinking,
                    0,
                    [metadata]),
                new StreamedChunk("Final answer", false, null, ContentBlockType.Text, 1),
                new StreamedChunk(null, true, new UsageInfo(12, 5, 17))
            ], call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.BlockType == ContentBlockType.Thinking &&
                chunk.BlockIndex == 0 &&
                chunk.Content == "Let me think"),
            Arg.Any<CancellationToken>());

        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.BlockType == ContentBlockType.Text &&
                chunk.BlockIndex == 1 &&
                chunk.Content == "Final answer"),
            Arg.Any<CancellationToken>());

        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsMixedThinkingAndTextCompletion(events)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_PreStreamEvents_RecordsBeforeStreaming()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var modelChanged = new ModelChanged(conversationId, "new/model");
        var chatRequest = CreateChatRequest("new/model");
        SetupPreparedTurn(conversationId, chatRequest, [modelChanged]);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("response", false, null),
                new StreamedChunk(null, true, new UsageInfo(1, 1, 2))
            ], call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "new/model", true, null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => events.Single() == modelChanged),
            Arg.Any<CancellationToken>());

        _chatProvider.Received(1).StreamAsync(chatRequest, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ProviderFailure_RecordsTurnFailedAndSendsError()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new ChatRateLimitException("Raw provider message."),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsRateLimitFailure(events)),
            Arg.Any<CancellationToken>());

        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.ConversationId == conversationId &&
                chunk.BlockType == ContentBlockType.Text &&
                chunk.BlockIndex == 0 &&
                chunk.Content == "partial"),
            Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "rate_limited",
            "The AI provider rate limit was exceeded.",
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_UserCancellation_RecordsTurnCancelledWithPartialContent()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        // The registry reports this cancellation was USER-initiated ("stop generating").
        _activeTurns.WasUserCancelled(conversationId).Returns(true);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new OperationCanceledException(),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsCancellation(events)),
            CancellationToken.None);

        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.ConversationId == conversationId &&
                chunk.BlockType == ContentBlockType.Text &&
                chunk.BlockIndex == 0 &&
                chunk.Content == "partial"),
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendErrorAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_NonUserCancellation_RecordsNothingAndRethrows()
    {
        // Arrange — a host-shutdown interrupt: the token fires but the registry does
        // NOT report a user cancel (default substitute returns false).
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new OperationCanceledException(),
                call.ArgAt<CancellationToken>(1)));

        // Act
        var act = () => sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null, TestContext.Current.CancellationToken);

        // Assert — the OCE propagates so the worker leaves the row for orphan recovery.
        await act.Should().ThrowAsync<OperationCanceledException>();

        // No terminal event of any kind is recorded (not TurnCancelled, not TurnFailed).
        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<TurnCancelled>().Any() ||
                events.OfType<TurnFailed>().Any()),
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_UnexpectedException_RecordsTurnFailedAndDoesNotEmitSuccessEvents()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamThenThrow(
                new InvalidOperationException("Something completely unexpected"),
                call.ArgAt<CancellationToken>(1)));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsInternalFailure(events)),
            CancellationToken.None);

        await _notifier.Received(1).SendChunkAsync(
            conversationId,
            Arg.Is<ChatStreamChunkDto>(chunk =>
                chunk.ConversationId == conversationId &&
                chunk.BlockType == ContentBlockType.Text &&
                chunk.BlockIndex == 0 &&
                chunk.Content == "partial"),
            Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "internal_error",
            "An unexpected error occurred.",
            CancellationToken.None);
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());

        await _eventRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(),
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<AssistantResponseCompleted>().Any() ||
                events.OfType<TurnCompleted>().Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_PersonaNotFound_RecordsTurnFailedAndDoesNotStream()
    {
        // Arrange
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var personaId = Guid.NewGuid();

        _turnPreparer.PrepareAsync(Arg.Any<Guid>(), conversationId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<ModelParameters?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PreparedConversationTurn>>(_ => throw new ConversationPersonaNotFoundException(conversationId, personaId));

        // Act
        await sut.StreamAsync(Guid.NewGuid(), conversationId, Guid.NewGuid(), "fallback/model", false, null, TestContext.Current.CancellationToken);

        // Assert
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => ContainsPersonaNotFoundFailure(events)),
            CancellationToken.None);

        await _notifier.Received(1).SendErrorAsync(
            conversationId,
            "persona_not_found",
            "The persona for this conversation no longer exists.",
            CancellationToken.None);
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
        _chatProvider.DidNotReceive().StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ToolRound_RecordsRoundBeforeExecutingTool()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        var calls = new List<string>();
        var providerRound = 0;
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++providerRound == 1
                ? StreamChunks([
                    new StreamedChunk(
                        null,
                        true,
                        new UsageInfo(100, 20, 120),
                        ToolCalls: [new ToolCall("call-1", "get_current_time", "{}", "fc-1")],
                        FinishReason: FinishReason.ToolCalls)
                ], call.ArgAt<CancellationToken>(1))
                : StreamChunks([
                    new StreamedChunk("It is nine.", false, null),
                    new StreamedChunk(null, true, new UsageInfo(150, 80, 230), FinishReason: FinishReason.Stop)
                ], call.ArgAt<CancellationToken>(1)));
        _eventRecorder.When(recorder => recorder.RecordAsync(
                conversationId,
                Arg.Is<IEnumerable<ConversationEvent>>(events =>
                    events.OfType<AssistantResponseCompleted>()
                        .Any(response => response.FinishReason == FinishReason.ToolCalls)),
                Arg.Any<CancellationToken>()))
            .Do(_ => calls.Add("round-recorded"));
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("tool-executed");
                return new ToolResult("{\"utc\":\"09:00Z\"}", "09:00 UTC", ToolExecutionStatus.Succeeded);
            });

        await sut.StreamAsync(
            Guid.NewGuid(),
            conversationId,
            Guid.NewGuid(),
            "test/model",
            false,
            null,
            TestContext.Current.CancellationToken);

        calls.Should().Equal("round-recorded", "tool-executed");
    }

    [Fact]
    public async Task StreamAsync_ToolRound_SecondRequestContainsCallAndResult()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        var requests = new List<ChatRequest>();
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<ChatRequest>());
                return requests.Count == 1
                    ? StreamChunks([
                        new StreamedChunk(
                            null,
                            true,
                            new UsageInfo(1, 1, 2),
                            ToolCalls: [new ToolCall("call-1", "get_current_time", "{}", "fc-1")],
                            FinishReason: FinishReason.ToolCalls)
                    ], call.ArgAt<CancellationToken>(1))
                    : StreamChunks([
                        new StreamedChunk("Done", false, null),
                        new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
                    ], call.ArgAt<CancellationToken>(1));
            });
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult("{\"utc\":\"09:00Z\"}", "09:00 UTC", ToolExecutionStatus.Succeeded));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        requests.Should().HaveCount(2);
        var followUp = requests[1].Messages;
        followUp[^2].ContentBlocks.Should().ContainSingle(block =>
            block.Type == ContentBlockType.ToolUse &&
            block.ToolCallId == "call-1");
        followUp[^1].Role.Should().Be(ChatRole.Tool);
        followUp[^1].ToolResultContent.Should().Be("{\"utc\":\"09:00Z\"}");
    }

    [Fact]
    public async Task StreamAsync_TwoToolCalls_ExecutesAndReturnsBothInProviderOrder()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        var requests = new List<ChatRequest>();
        var executedCalls = new List<string>();

        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<ChatRequest>());
                return requests.Count == 1
                    ? StreamChunks([
                        new StreamedChunk(
                            null,
                            true,
                            new UsageInfo(2, 2, 4),
                            ToolCalls:
                            [
                                new ToolCall("call-1", "first_tool", "{}"),
                                new ToolCall("call-2", "second_tool", "{}"),
                            ],
                            FinishReason: FinishReason.ToolCalls)
                    ], call.ArgAt<CancellationToken>(1))
                    : StreamChunks([
                        new StreamedChunk("Done", false, null),
                        new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
                    ], call.ArgAt<CancellationToken>(1));
            });
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var toolCall = call.Arg<ToolCall>();
                executedCalls.Add(toolCall.Id);
                return new ToolResult($"{{\"call\":\"{toolCall.Id}\"}}", null, ToolExecutionStatus.Succeeded);
            });

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        executedCalls.Should().Equal("call-1", "call-2");
        await _toolExecutionRecorder.Received(2).RecordAsync(
            conversationId,
            Arg.Any<Guid>(),
            Arg.Any<ToolCall>(),
            Arg.Any<ToolResult>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
        requests[1].Messages.TakeLast(3).Select(m => m.Role)
            .Should().Equal(ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool);
        requests[1].Messages.TakeLast(2).Select(m => m.ContentBlocks.Single().ToolCallId)
            .Should().Equal("call-1", "call-2");
    }

    [Fact]
    public async Task StreamAsync_MultiRoundTurn_SumsUsageAndKeepsLastRoundInput()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        var providerRound = 0;
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++providerRound == 1
                ? StreamChunks([
                    new StreamedChunk(
                        null,
                        true,
                        new UsageInfo(100, 20, 120),
                        ToolCalls: [new ToolCall("call-1", "get_current_time", "{}")],
                        FinishReason: FinishReason.ToolCalls)
                ], call.ArgAt<CancellationToken>(1))
                : StreamChunks([
                    new StreamedChunk("Done", false, null),
                    new StreamedChunk(null, true, new UsageInfo(150, 80, 230), FinishReason: FinishReason.Stop)
                ], call.ArgAt<CancellationToken>(1)));
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult("{}", null, ToolExecutionStatus.Succeeded));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => HasExpectedMultiRoundUsage(events)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_FailedToolResult_StillCompletesTurnWithoutTurnFailed()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        var providerRound = 0;
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++providerRound == 1
                ? StreamChunks([
                    new StreamedChunk(
                        null,
                        true,
                        new UsageInfo(1, 1, 2),
                        ToolCalls: [new ToolCall("call-1", "get_current_time", "{}")],
                        FinishReason: FinishReason.ToolCalls)
                ], call.ArgAt<CancellationToken>(1))
                : StreamChunks([
                    new StreamedChunk("I could not check the time.", false, null),
                    new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
                ], call.ArgAt<CancellationToken>(1)));
        var failedResult = new ToolResult(
            "{\"error\":\"clock exploded\"}",
            "clock exploded",
            ToolExecutionStatus.Failed);
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(failedResult);

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        await _toolExecutionRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Any<Guid>(),
            Arg.Any<ToolCall>(),
            failedResult,
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
        await _eventRecorder.DidNotReceive().RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events => events.OfType<TurnFailed>().Any()),
            Arg.Any<CancellationToken>());
        await _notifier.Received(1).SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ProviderFailureAfterToolRound_RecordsTurnFailed()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        var providerRound = 0;
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++providerRound == 1
                ? StreamChunks([
                    new StreamedChunk(
                        null,
                        true,
                        new UsageInfo(1, 1, 2),
                        ToolCalls: [new ToolCall("call-1", "get_current_time", "{}")],
                        FinishReason: FinishReason.ToolCalls)
                ], call.ArgAt<CancellationToken>(1))
                : StreamThenThrow(
                    new ChatProviderException("round two failed"),
                    call.ArgAt<CancellationToken>(1)));
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult("{}", null, ToolExecutionStatus.Succeeded));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        await _toolExecutionRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Any<Guid>(),
            Arg.Any<ToolCall>(),
            Arg.Any<ToolResult>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<TurnFailed>().Any(failed => failed.ErrorCode == "provider_error")),
            CancellationToken.None);
        await _notifier.DidNotReceive().SendCompletedAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_CancelledDuringToolExecution_RecordsTurnCancelled()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);
        _activeTurns.WasUserCancelled(conversationId).Returns(true);
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk(
                    null,
                    true,
                    new UsageInfo(1, 1, 2),
                    ToolCalls: [new ToolCall("call-1", "get_current_time", "{}")],
                    FinishReason: FinishReason.ToolCalls)
            ], call.ArgAt<CancellationToken>(1)));
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<ToolResult>>(_ => throw new OperationCanceledException());

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        await _toolRegistry.Received(1).ExecuteAsync(
            Arg.Any<ToolCall>(),
            Arg.Any<ToolContext>(),
            TestContext.Current.CancellationToken);
        await _eventRecorder.Received(1).RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<TurnCancelled>().Single().PartialContent == null),
            CancellationToken.None);
        _chatProvider.Received(1).StreamAsync(
            Arg.Any<ChatRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ResumeMissingToolResult_ExecutesBeforeProviderWithoutRecordingRoundAgain()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var request = new ChatRequest(
            "test/model",
            [
                new ChatMessage(ChatRole.User, MessageContentBlocks.Text("What time is it?")),
                new ChatMessage(
                    ChatRole.Assistant,
                    [MessageContentBlock.ToolUse("call-1", "get_current_time", "{}")])
            ]);
        SetupPreparedTurn(conversationId, request);
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult("{\"utc\":\"09:00Z\"}", null, ToolExecutionStatus.Succeeded));
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("It is nine.", false, null),
                new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
            ], call.ArgAt<CancellationToken>(1)));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, messageId, "test/model", false, null,
            TestContext.Current.CancellationToken);

        await _toolRegistry.Received(1).ExecuteAsync(
            Arg.Is<ToolCall>(call => call.Id == "call-1"),
            Arg.Any<ToolContext>(),
            Arg.Any<CancellationToken>());
        await _eventRecorder.DidNotReceive().RecordAsync(
            conversationId,
            Arg.Is<IEnumerable<ConversationEvent>>(events =>
                events.OfType<AssistantResponseCompleted>()
                    .Any(response => response.FinishReason == FinishReason.ToolCalls)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_ResumeExistingToolResult_SkipsExecution()
    {
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var request = new ChatRequest(
            "test/model",
            [
                new ChatMessage(ChatRole.User, MessageContentBlocks.Text("What time is it?")),
                new ChatMessage(ChatRole.Assistant, [MessageContentBlock.ToolUse("call-1", "get_current_time", "{}")]),
                new ChatMessage(
                    ChatRole.Tool,
                    [MessageContentBlock.ToolResult("call-1", Guid.NewGuid())],
                    "{\"utc\":\"09:00Z\"}")
            ]);
        SetupPreparedTurn(conversationId, request);
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => StreamChunks([
                new StreamedChunk("It is nine.", false, null),
                new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
            ], call.ArgAt<CancellationToken>(1)));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        await _toolRegistry.DidNotReceive().ExecuteAsync(
            Arg.Any<ToolCall>(),
            Arg.Any<ToolContext>(),
            Arg.Any<CancellationToken>());
        _chatProvider.Received(1).StreamAsync(
            Arg.Is<ChatRequest>(sent => sent.Messages.Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamAsync_MultiRoundTurn_ChunksCarryPerRoundMessageIdMatchingRecordedRound()
    {
        // QRE-402 contract: every streamed chunk's MessageId is the id of the
        // conversation_messages row it belongs to. Each model round streams under
        // its own id, which is also that round's AssistantResponseCompleted.Id.
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        var turnMessageId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        var chunks = new List<ChatStreamChunkDto>();
        _notifier.SendChunkAsync(
            Arg.Any<Guid>(),
            Arg.Do<ChatStreamChunkDto>(chunks.Add),
            Arg.Any<CancellationToken>());

        var recordedRounds = new List<AssistantResponseCompleted>();
        _eventRecorder.RecordAsync(
            conversationId,
            Arg.Do<IEnumerable<ConversationEvent>>(events =>
                recordedRounds.AddRange(events.OfType<AssistantResponseCompleted>())),
            Arg.Any<CancellationToken>());

        var providerRound = 0;
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++providerRound == 1
                ? StreamChunks([
                    new StreamedChunk("Checking the time.", false, null),
                    new StreamedChunk(
                        null,
                        true,
                        new UsageInfo(1, 1, 2),
                        ToolCalls: [new ToolCall("call-1", "get_current_time", "{}")],
                        FinishReason: FinishReason.ToolCalls)
                ], call.ArgAt<CancellationToken>(1))
                : StreamChunks([
                    new StreamedChunk("It is nine.", false, null),
                    new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
                ], call.ArgAt<CancellationToken>(1)));
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult("{}", null, ToolExecutionStatus.Succeeded));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, turnMessageId, "test/model", false, null,
            TestContext.Current.CancellationToken);

        recordedRounds.Should().HaveCount(2);

        var roundOneText = chunks.Single(c => c.Content == "Checking the time.");
        var toolUseChunk = chunks.Single(c => c.BlockType == ContentBlockType.ToolUse);
        var roundTwoText = chunks.Single(c => c.Content == "It is nine.");

        roundOneText.MessageId.Should().Be(recordedRounds[0].Id);
        toolUseChunk.MessageId.Should().Be(recordedRounds[0].Id);
        roundTwoText.MessageId.Should().Be(recordedRounds[1].Id);
        roundTwoText.MessageId.Should().NotBe(roundOneText.MessageId);
        chunks.Should().OnlyContain(c => c.MessageId != turnMessageId);
    }

    [Fact]
    public async Task StreamAsync_ToolResultChunk_CarriesPayloadIdAsMessageId()
    {
        // QRE-402 contract: the tool_result chunk's MessageId is the Tool-role
        // message row's id, which the ToolExecutedProjector sets to the PayloadId.
        var sut = CreateSut();
        var conversationId = Guid.NewGuid();
        SetupPreparedTurn(conversationId);

        var chunks = new List<ChatStreamChunkDto>();
        _notifier.SendChunkAsync(
            Arg.Any<Guid>(),
            Arg.Do<ChatStreamChunkDto>(chunks.Add),
            Arg.Any<CancellationToken>());

        var providerRound = 0;
        _chatProvider.StreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ++providerRound == 1
                ? StreamChunks([
                    new StreamedChunk(
                        null,
                        true,
                        new UsageInfo(1, 1, 2),
                        ToolCalls: [new ToolCall("call-1", "get_current_time", "{}")],
                        FinishReason: FinishReason.ToolCalls)
                ], call.ArgAt<CancellationToken>(1))
                : StreamChunks([
                    new StreamedChunk("Done", false, null),
                    new StreamedChunk(null, true, new UsageInfo(1, 1, 2), FinishReason: FinishReason.Stop)
                ], call.ArgAt<CancellationToken>(1)));
        _toolRegistry.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult("{\"utc\":\"09:00Z\"}", "09:00 UTC", ToolExecutionStatus.Succeeded));

        await sut.StreamAsync(
            Guid.NewGuid(), conversationId, Guid.NewGuid(), "test/model", false, null,
            TestContext.Current.CancellationToken);

        var toolResultChunk = chunks.Single(c => c.BlockType == ContentBlockType.ToolResult);
        toolResultChunk.PayloadId.Should().NotBeNull().And.NotBe(Guid.Empty);
        toolResultChunk.MessageId.Should().Be(toolResultChunk.PayloadId!.Value);
    }

    private void SetupPreparedTurn(
        Guid conversationId,
        ChatRequest? chatRequest = null,
        IReadOnlyList<ConversationEvent>? preStreamEvents = null)
    {
        _turnPreparer.PrepareAsync(Arg.Any<Guid>(), conversationId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<ModelParameters?>(), Arg.Any<CancellationToken>())
            .Returns(new PreparedConversationTurn(
                Guid.NewGuid(),
                chatRequest ?? CreateChatRequest(),
                preStreamEvents ?? []));
    }

    private static ChatRequest CreateChatRequest(string model = "test/model") =>
        new(
            model,
            [new ChatMessage(ChatRole.User, MessageContentBlocks.Text("Hello"))],
            null,
            null);

    private static bool ContainsCompletionEvents(IEnumerable<ConversationEvent> events)
    {
        var eventList = events.ToList();
        var response = eventList.OfType<AssistantResponseCompleted>().SingleOrDefault();
        var turn = eventList.OfType<TurnCompleted>().SingleOrDefault();

        return response is not null &&
            MessageContentBlocks.ToVisibleText(response.ContentBlocks) == "Hello there" &&
            response.Model == "test/model" &&
            turn?.InputTokens == 12 &&
            turn.OutputTokens == 5 &&
            eventList.All(e => e is not MessageSent);
    }

    private static bool HasExpectedMultiRoundUsage(IEnumerable<ConversationEvent> events)
    {
        var completed = events.OfType<TurnCompleted>().SingleOrDefault();
        return completed is not null
            && completed.InputTokens == 250
            && completed.OutputTokens == 100
            && completed.LastRoundInputTokens == 150;
    }

    private static bool ContainsMixedThinkingAndTextCompletion(IEnumerable<ConversationEvent> events)
    {
        var response = events.OfType<AssistantResponseCompleted>().SingleOrDefault();
        if (response is null)
            return false;

        if (response.ContentBlocks.Count != 2)
            return false;

        var thinking = response.ContentBlocks[0];
        var text = response.ContentBlocks[1];

        return thinking.Type == ContentBlockType.Thinking &&
            thinking.Content == "Let me think" &&
            thinking.ProviderMetadata?[0]["signature"] as string == "sig-123" &&
            text.Type == ContentBlockType.Text &&
            text.Content == "Final answer";
    }

    private static bool ContainsRateLimitFailure(IEnumerable<ConversationEvent> events)
    {
        var failed = events.SingleOrDefault() as TurnFailed;

        return failed?.Source == FailureSource.Provider &&
            failed.ErrorCode == "rate_limited" &&
            failed.Message == "The AI provider rate limit was exceeded." &&
            failed.PartialContent == "partial";
    }

    private static bool ContainsInternalFailure(IEnumerable<ConversationEvent> events)
    {
        var failed = events.SingleOrDefault() as TurnFailed;

        return failed?.Source == FailureSource.Internal &&
            failed.ErrorCode == "internal_error" &&
            failed.Message == "An unexpected error occurred." &&
            failed.PartialContent == "partial";
    }

    private static bool ContainsCancellation(IEnumerable<ConversationEvent> events)
    {
        var cancelled = events.SingleOrDefault() as TurnCancelled;
        return cancelled?.PartialContent == "partial";
    }

    private static bool ContainsPersonaNotFoundFailure(IEnumerable<ConversationEvent> events)
    {
        var failed = events.SingleOrDefault() as TurnFailed;

        return failed?.Source == FailureSource.Internal &&
            failed.ErrorCode == "persona_not_found" &&
            failed.Message == "The persona for this conversation no longer exists." &&
            failed.PartialContent is null;
    }

    private static async IAsyncEnumerable<StreamedChunk> StreamChunks(
        IEnumerable<StreamedChunk> chunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<StreamedChunk> StreamThenThrow(
        Exception exception,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new StreamedChunk("partial", false, null);
        await Task.Yield();
        throw exception;
    }
}
