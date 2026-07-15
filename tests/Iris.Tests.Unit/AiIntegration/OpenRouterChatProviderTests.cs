using FluentAssertions;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;
using Iris.Infrastructure.AiIntegration;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Iris.Tests.Unit.AiIntegration;

public class OpenRouterChatProviderTests
{
    // --- Helpers ---

    private static ChatRequest CreateRequest(
        string model = "test/model",
        string? systemPrompt = null,
        ModelParameters? modelParameters = null,
        ToolOptions? toolOptions = null,
        IReadOnlyList<ChatMessage>? messages = null)
    {
        return new ChatRequest(
            Model: model,
            Messages: messages ?? [new ChatMessage(ChatRole.User, MessageContentBlocks.Text("Hello"))],
            SystemPrompt: systemPrompt,
            ModelParameters: modelParameters,
            ToolOptions: toolOptions
        );
    }

    private static HttpResponseMessage CreateStreamResponse(params string[] events)
    {
        var sse = string.Join("\n\n", events) + "\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    }

    private static string DeltaEvent(string text) =>
        $"event: response.output_text.delta\ndata: {{\"type\":\"response.output_text.delta\",\"delta\":\"{text}\"}}";

    private static string ReasoningDeltaEvent(string text) =>
        $"event: response.reasoning.delta\ndata: {{\"type\":\"response.reasoning.delta\",\"delta\":\"{text}\"}}";

    private static string ReasoningDetailsEvent(string text, string id = "reasoning-text-1") =>
        "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[" +
        $"{{\"type\":\"reasoning.text\",\"text\":\"{text}\",\"signature\":\"sig-123\",\"id\":\"{id}\",\"format\":\"anthropic-claude-v1\",\"index\":0}}" +
        "]}}]}";

    private static string CompletedEvent(int input = 10, int output = 5, int total = 15) =>
        $"event: response.completed\ndata: {{\"type\":\"response.completed\",\"response\":{{\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},\"total_tokens\":{total}}}}}}}";

    private static string DoneEvent() => "data: [DONE]";

    private static string FunctionCallAddedEvent(
        int outputIndex,
        string itemId,
        string callId,
        string name,
        string arguments = "") =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            output_index = outputIndex,
            item = new
            {
                type = "function_call",
                id = itemId,
                call_id = callId,
                name,
                arguments
            }
        });

    private static string FunctionCallDeltaEvent(
        int outputIndex,
        string itemId,
        string delta) =>
        "data: " + JsonSerializer.Serialize(new
        {
            type = "response.function_call_arguments.delta",
            output_index = outputIndex,
            item_id = itemId,
            delta
        });

    private static JsonElement EmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }

    private static (OpenRouterChatProvider sut, MockHttpHandler handler) CreateProvider(
        HttpResponseMessage response)
    {
        var handler = new MockHttpHandler(response);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.ai")
        };
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-key");
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://iris.qreedence.com");
        client.DefaultRequestHeaders.Add("X-OpenRouter-Title", "Iris");
        return (new OpenRouterChatProvider(client), handler);
    }

    private static OpenRouterChatProvider CreateProviderWithHandler(MockHttpHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.ai")
        };
        return new OpenRouterChatProvider(client);
    }

    private class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            throw new TaskCanceledException("Request timed out", new TimeoutException());
        }
    }

    // --- §3: Streaming ---

    [Fact]
    public async Task StreamAsync_YieldsChunks()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hello"),
            DeltaEvent(" world"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks[0].Content.Should().Be("Hello");
        chunks[1].Content.Should().Be(" world");
    }

    [Fact]
    public async Task StreamAsync_ReasoningDelta_YieldsThinkingChunk()
    {
        var response = CreateStreamResponse(
            ReasoningDeltaEvent("Let me think"),
            DeltaEvent("Answer"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks[0].BlockType.Should().Be(ContentBlockType.Thinking);
        chunks[0].BlockIndex.Should().Be(0);
        chunks[0].Content.Should().Be("Let me think");

        chunks[1].BlockType.Should().Be(ContentBlockType.Text);
        chunks[1].BlockIndex.Should().Be(1);
        chunks[1].Content.Should().Be("Answer");
    }

    [Fact]
    public async Task StreamAsync_RepeatedBlockTypes_ReusesOneBlockIndexPerBlockType()
    {
        var response = CreateStreamResponse(
            ReasoningDeltaEvent("Think "),
            ReasoningDeltaEvent("again"),
            DeltaEvent("Answer "),
            DeltaEvent("now"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks.Take(4).Select(c => c.BlockType).Should().Equal(
            ContentBlockType.Thinking,
            ContentBlockType.Thinking,
            ContentBlockType.Text,
            ContentBlockType.Text);
        chunks.Take(4).Select(c => c.BlockIndex).Should().Equal(0, 0, 1, 1);
    }

    [Fact]
    public async Task StreamAsync_ReasoningDetails_PreservesProviderMetadata()
    {
        var response = CreateStreamResponse(
            ReasoningDetailsEvent("Reasoning text"),
            DeltaEvent("Answer"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var thinking = chunks[0];
        thinking.BlockType.Should().Be(ContentBlockType.Thinking);
        thinking.Content.Should().Be("Reasoning text");
        thinking.ProviderMetadata.Should().NotBeNull();
        var metadata = thinking.ProviderMetadata![0];
        metadata["type"].Should().Be("reasoning.text");
        metadata["signature"].Should().Be("sig-123");
    }

    [Fact]
    public async Task StreamAsync_FinalChunk_HasIsComplete()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hi"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks[0].IsComplete.Should().BeFalse();
        chunks.Last().IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_FinalChunk_IncludesUsageInfo()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hi"),
            CompletedEvent(20, 10, 30));
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var final = chunks.Last();
        final.UsageInfo.Should().NotBeNull();
        final.UsageInfo!.InputTokens.Should().Be(20);
        final.UsageInfo.OutputTokens.Should().Be(10);
        final.UsageInfo.TotalTokens.Should().Be(30);
    }

    [Fact]
    public async Task StreamAsync_Request_PassesBackPreservedReasoningDetails()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = "reasoning.text",
            ["text"] = "preserved reasoning",
            ["signature"] = "sig-123",
            ["id"] = "reasoning-text-1",
            ["format"] = "anthropic-claude-v1",
            ["index"] = 0
        };
        var request = new ChatRequest(
            "test/model",
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        MessageContentBlock.Thinking("preserved reasoning", [metadata]),
                        MessageContentBlock.Text("Final answer")
                    ]),
                new ChatMessage(ChatRole.User, MessageContentBlocks.Text("Continue"))
            ]);

        var response = CreateStreamResponse(CompletedEvent());
        var (sut, handler) = CreateProvider(response);

        await foreach (var _ in sut.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        handler.LastRequestBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var assistantMessage = body.RootElement.GetProperty("input")[0];

        assistantMessage.GetProperty("content").GetString().Should().Be("Final answer");
        var reasoningDetails = assistantMessage.GetProperty("reasoning_details");
        reasoningDetails.ValueKind.Should().Be(JsonValueKind.Array);
        reasoningDetails[0].GetProperty("type").GetString().Should().Be("reasoning.text");
        reasoningDetails[0].GetProperty("signature").GetString().Should().Be("sig-123");
    }

    [Fact]
    public async Task StreamAsync_Request_PassesBackReasoningDetailsAfterJsonRoundTrip()
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"type\":\"reasoning.text\",\"text\":\"persisted reasoning\",\"signature\":\"sig-456\",\"id\":\"reasoning-text-2\",\"format\":\"anthropic-claude-v1\",\"index\":0}")!;
        var request = new ChatRequest(
            "test/model",
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        MessageContentBlock.Thinking("persisted reasoning", [metadata]),
                        MessageContentBlock.Text("Final answer")
                    ]),
                new ChatMessage(ChatRole.User, MessageContentBlocks.Text("Continue"))
            ]);

        var response = CreateStreamResponse(CompletedEvent());
        var (sut, handler) = CreateProvider(response);

        await foreach (var _ in sut.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        handler.LastRequestBody.Should().NotBeNull();
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var assistantMessage = body.RootElement.GetProperty("input")[0];
        var reasoningDetails = assistantMessage.GetProperty("reasoning_details");

        reasoningDetails.ValueKind.Should().Be(JsonValueKind.Array);
        reasoningDetails[0].GetProperty("type").GetString().Should().Be("reasoning.text");
        reasoningDetails[0].GetProperty("text").GetString().Should().Be("persisted reasoning");
        reasoningDetails[0].GetProperty("signature").GetString().Should().Be("sig-456");
    }

    [Fact]
    public async Task StreamAsync_EmptyStream_ThrowsProviderException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/event-stream")
        };
        var (sut, _) = CreateProvider(response);

        var act = async () =>
        {
            await foreach (var _ in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<ChatProviderException>()
            .WithMessage("*without response.completed*");
    }

    [Fact]
    public async Task StreamAsync_ResponseFailed_ThrowsProviderException()
    {
        var response = CreateStreamResponse(
            "data: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"Provider disconnected\"},\"error_type\":\"provider_unavailable\"}}");
        var (sut, _) = CreateProvider(response);

        var act = async () =>
        {
            await foreach (var _ in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<ChatProviderException>()
            .WithMessage("*provider_unavailable*Provider disconnected*");
    }

    [Fact]
    public async Task StreamAsync_DoneSentinel_HandlesGracefully()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hi"),
            CompletedEvent(),
            DoneEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks.Should().HaveCount(2);
        chunks.Last().IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_NullToolOptions_OmitsToolFieldsFromRequest()
    {
        var (sut, handler) = CreateProvider(CreateStreamResponse(CompletedEvent()));

        await foreach (var _ in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken)) { }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.TryGetProperty("tools", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("tool_choice", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_WithTools_SerializesResponsesApiToolFormat()
    {
        var tool = new ToolDefinition("get_current_time", "Get the current time.", EmptyObjectSchema());
        var request = CreateRequest(toolOptions: new ToolOptions([tool], ToolChoice.Auto));
        var (sut, handler) = CreateProvider(CreateStreamResponse(CompletedEvent()));

        await foreach (var _ in sut.StreamAsync(request, TestContext.Current.CancellationToken)) { }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var serializedTool = body.RootElement.GetProperty("tools")[0];
        serializedTool.GetProperty("type").GetString().Should().Be("function");
        serializedTool.GetProperty("name").GetString().Should().Be(tool.Name);
        serializedTool.GetProperty("description").GetString().Should().Be(tool.Description);
        serializedTool.GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");
        serializedTool.TryGetProperty("function", out _).Should().BeFalse(
            "the Responses API uses flat function definitions, unlike legacy Chat Completions");
        body.RootElement.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task StreamAsync_ToolChoice_SerializesAutoNoneAndSpecific()
    {
        var tool = new ToolDefinition("get_current_time", "Get the current time.", EmptyObjectSchema());
        var choices = new[] { ToolChoice.Auto, ToolChoice.None, ToolChoice.Specific(tool.Name) };
        var handler = new MockHttpHandler(choices.Select(_ => CreateStreamResponse(CompletedEvent())).ToArray());
        var sut = CreateProviderWithHandler(handler);
        var bodies = new List<JsonElement>();

        foreach (var choice in choices)
        {
            await foreach (var _ in sut.StreamAsync(
                CreateRequest(toolOptions: new ToolOptions([tool], choice)),
                TestContext.Current.CancellationToken)) { }

            using var document = JsonDocument.Parse(handler.LastRequestBody!);
            bodies.Add(document.RootElement.Clone());
        }

        bodies[0].GetProperty("tool_choice").GetString().Should().Be("auto");
        bodies[1].GetProperty("tool_choice").GetString().Should().Be("none");
        bodies[2].GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("function");
        bodies[2].GetProperty("tool_choice").GetProperty("name").GetString().Should().Be(tool.Name);
    }

    [Fact]
    public async Task StreamAsync_FollowUpRequest_SerializesFunctionCallAndOutputItems()
    {
        var messages = new ChatMessage[]
        {
            new(
                ChatRole.Assistant,
                [MessageContentBlock.ToolUse(
                    "call-123",
                    "get_current_time",
                    "{}",
                    [new Dictionary<string, object?> { ["item_id"] = "fc-123" }])]),
            new(
                ChatRole.Tool,
                [MessageContentBlock.ToolResult("call-123", Guid.NewGuid())],
                "{\"time\":\"10:30:00Z\"}")
        };
        var (sut, handler) = CreateProvider(CreateStreamResponse(CompletedEvent()));

        await foreach (var _ in sut.StreamAsync(
            CreateRequest(messages: messages),
            TestContext.Current.CancellationToken)) { }

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var input = body.RootElement.GetProperty("input");
        input[0].GetProperty("type").GetString().Should().Be("function_call");
        input[0].GetProperty("id").GetString().Should().Be("fc-123");
        input[0].GetProperty("call_id").GetString().Should().Be("call-123");
        input[1].GetProperty("type").GetString().Should().Be("function_call_output");
        input[1].GetProperty("call_id").GetString().Should().Be("call-123");
        input[1].GetProperty("output").GetString().Should().Be("{\"time\":\"10:30:00Z\"}");
    }

    [Fact]
    public async Task StreamAsync_ToolCallArgumentDeltas_AccumulatesCompleteJson()
    {
        var response = CreateStreamResponse(
            FunctionCallAddedEvent(0, "fc-1", "call-1", "get_weather"),
            FunctionCallDeltaEvent(0, "fc-1", "{\"loc"),
            FunctionCallDeltaEvent(0, "fc-1", "ation\":\"Stock"),
            FunctionCallDeltaEvent(0, "fc-1", "holm\"}"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var completed = chunks.Should().ContainSingle(c => c.IsComplete).Subject;
        completed.FinishReason.Should().Be(FinishReason.ToolCalls);
        var call = completed.ToolCalls.Should().ContainSingle().Subject;
        call.Id.Should().Be("call-1");
        call.ProviderItemId.Should().Be("fc-1");
        call.FunctionName.Should().Be("get_weather");
        call.ArgumentsJson.Should().Be("{\"location\":\"Stockholm\"}");
    }

    [Fact]
    public async Task StreamAsync_MultipleInterleavedToolCalls_AccumulatesByOutputIndex()
    {
        var response = CreateStreamResponse(
            FunctionCallAddedEvent(0, "fc-1", "call-1", "first"),
            FunctionCallAddedEvent(1, "fc-2", "call-2", "second"),
            FunctionCallDeltaEvent(1, "fc-2", "{\"b\":"),
            FunctionCallDeltaEvent(0, "fc-1", "{\"a\":"),
            FunctionCallDeltaEvent(1, "fc-2", "2}"),
            FunctionCallDeltaEvent(0, "fc-1", "1}"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var calls = chunks.Single(c => c.IsComplete).ToolCalls!;
        calls.Select(c => c.Id).Should().Equal("call-1", "call-2");
        calls.Select(c => c.ArgumentsJson).Should().Equal("{\"a\":1}", "{\"b\":2}");
    }

    [Fact]
    public async Task StreamAsync_NoToolCalls_FinalChunkHasStopFinishReason()
    {
        var (sut, _) = CreateProvider(CreateStreamResponse(DeltaEvent("Hi"), CompletedEvent()));

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var completed = chunks.Single(c => c.IsComplete);
        completed.FinishReason.Should().Be(FinishReason.Stop);
        completed.ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_ToolDeltaBeforeAdded_ThrowsDeserializationException()
    {
        var response = CreateStreamResponse(FunctionCallDeltaEvent(0, "fc-1", "{}"));
        var (sut, _) = CreateProvider(response);

        var act = async () =>
        {
            await foreach (var _ in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken)) { }
        };

        await act.Should().ThrowAsync<ChatDeserializationException>();
    }

    [Fact]
    public async Task StreamAsync_ThinkingAndToolCall_BothSurvive()
    {
        var response = CreateStreamResponse(
            ReasoningDeltaEvent("Checking the clock"),
            FunctionCallAddedEvent(0, "fc-1", "call-1", "get_current_time"),
            FunctionCallDeltaEvent(0, "fc-1", "{}"),
            CompletedEvent());
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.BlockType == ContentBlockType.Thinking && c.Content == "Checking the clock");
        chunks.Single(c => c.IsComplete).ToolCalls.Should().ContainSingle(c => c.Id == "call-1");
    }

    // --- §4: Error Handling ---

    [Fact]
    public async Task StreamAsync_Timeout_ThrowsTimeoutException()
    {
        var client = new HttpClient(new TimeoutHandler())
        {
            BaseAddress = new Uri("https://openrouter.ai")
        };
        var sut = new OpenRouterChatProvider(client);

        var act = async () =>
        {
            await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            { }
        };

        await act.Should().ThrowAsync<ChatTimeoutException>();
    }

    [Fact]
    public async Task StreamAsync_ServerError500_ThrowsProviderException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"server error\"}")
        });
        var sut = CreateProviderWithHandler(handler);

        var act = async () =>
        {
            await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            { }
        };

        await act.Should().ThrowAsync<ChatProviderException>();
    }

    [Fact]
    public async Task StreamAsync_MalformedEvent_ThrowsDeserializationException()
    {
        var response = CreateStreamResponse("data: not-json");
        var (sut, _) = CreateProvider(response);

        var act = async () =>
        {
            await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            { }
        };

        await act.Should().ThrowAsync<ChatDeserializationException>();
    }

    [Fact]
    public async Task StreamAsync_MalformedCompletedUsage_ThrowsDeserializationException()
    {
        var response = CreateStreamResponse(
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{}}}");
        var (sut, _) = CreateProvider(response);

        var act = async () =>
        {
            await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            { }
        };

        await act.Should().ThrowAsync<ChatDeserializationException>();
    }
}
