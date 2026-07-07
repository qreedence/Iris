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
        ModelParameters? modelParameters = null)
    {
        return new ChatRequest(
            Model: model,
            Messages: [new ChatMessage(ChatRole.User, MessageContentBlocks.Text("Hello"))],
            SystemPrompt: systemPrompt,
            ModelParameters: modelParameters
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
    public async Task StreamAsync_EmptyStream_HandlesGracefully()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/event-stream")
        };
        var (sut, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in sut.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks.Should().BeEmpty();
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
