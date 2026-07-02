using FluentAssertions;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Domain.AiIntegration;
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
            Messages: [new ChatMessage(ChatRole.User, "Hello")],
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
