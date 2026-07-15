using FluentAssertions;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Conversations;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;
using Iris.Domain.Conversations.Events;
using NSubstitute;

namespace Iris.Tests.Unit.AiIntegration;

public class ToolExecutionRecorderTests
{
    [Fact]
    public async Task RecordAsync_AddsPayloadBeforeRecordingReferencingEvent()
    {
        var payloadStore = Substitute.For<IToolResultPayloadStore>();
        var eventRecorder = Substitute.For<IConversationEventRecorder>();
        var calls = new List<string>();
        ToolResultPayload? addedPayload = null;
        ToolExecuted? recordedEvent = null;

        payloadStore.When(store => store.Add(Arg.Any<ToolResultPayload>()))
            .Do(call =>
            {
                calls.Add("payload");
                addedPayload = call.Arg<ToolResultPayload>();
            });
        eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add("event");
                recordedEvent = call.Arg<IEnumerable<ConversationEvent>>()
                    .Single()
                    .Should().BeOfType<ToolExecuted>().Subject;
                return Task.FromResult<IReadOnlyList<RecordedEvent>>([]);
            });

        var now = DateTimeOffset.Parse("2026-07-14T09:00:00+00:00");
        var sut = new ToolExecutionRecorder(
            payloadStore,
            eventRecorder,
            new FixedTimeProvider(now));
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolCall = new ToolCall("call-1", "get_current_time", "{}");
        var result = new ToolResult(
            "{\"utc\":\"2026-07-14T09:00:00Z\"}",
            "09:00 UTC",
            ToolExecutionStatus.Succeeded);

        var recorded = await sut.RecordAsync(
            conversationId,
            messageId,
            toolCall,
            result,
            17,
            TestContext.Current.CancellationToken);

        calls.Should().Equal("payload", "event");
        addedPayload.Should().NotBeNull();
        addedPayload!.CreatedAt.Should().Be(now);
        addedPayload.PayloadJson.Should().Be(result.PayloadJson);
        recorded.Should().Be(recordedEvent);
        recorded.PayloadId.Should().Be(addedPayload.Id);
        recorded.MessageId.Should().Be(messageId);
        recorded.ToolCallId.Should().Be(toolCall.Id);
        recorded.Status.Should().Be(ToolExecutionStatus.Succeeded);
        recorded.DurationMs.Should().Be(17);
    }

    [Fact]
    public async Task RecordAsync_TruncatesPreviewToPersistenceLimit()
    {
        var payloadStore = Substitute.For<IToolResultPayloadStore>();
        var eventRecorder = Substitute.For<IConversationEventRecorder>();
        ToolResultPayload? payload = null;
        payloadStore.When(store => store.Add(Arg.Any<ToolResultPayload>()))
            .Do(call => payload = call.Arg<ToolResultPayload>());
        eventRecorder.RecordAsync(
                Arg.Any<Guid>(),
                Arg.Any<IEnumerable<ConversationEvent>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecordedEvent>>([]));
        var sut = new ToolExecutionRecorder(payloadStore, eventRecorder, TimeProvider.System);

        await sut.RecordAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolCall("call-1", "tool", "{}"),
            new ToolResult("{}", new string('x', 1200), ToolExecutionStatus.Failed),
            1,
            TestContext.Current.CancellationToken);

        payload!.Preview.Should().HaveLength(1000);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
