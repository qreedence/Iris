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

    private static HttpResponseMessage CreateCompletionResponse(
        string content = "Hello there!",
        int inputTokens = 10,
        int outputTokens = 5,
        int totalTokens = 15)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "resp_123",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new { type = "output_text", text = content }
                    }
                }
            },
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                total_tokens = totalTokens
            }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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

    private static (OpenRouterChatProvider provider, MockHttpHandler handler) CreateProvider(
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

    // --- §1: Request Serialization ---

    [Fact]
    public async Task CompleteAsync_SendsCorrectRequestShape()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/v1/responses", handler.LastRequest.RequestUri!.AbsolutePath);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("test/model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("user", body.RootElement.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("Hello", body.RootElement.GetProperty("input")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_WithModelParameters_IncludesInRequest()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(
            modelParameters: new ModelParameters(0.7f, 500, 0.9f)), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(0.7f, body.RootElement.GetProperty("temperature").GetSingle());
        Assert.Equal(500, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.9f, body.RootElement.GetProperty("top_p").GetSingle());
    }

    [Fact]
    public async Task CompleteAsync_WithSystemPrompt_IncludesInRequest()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(systemPrompt: "You are helpful."), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("You are helpful.", body.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task CompleteAsync_WithNullOptionals_OmitsFromJson()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(body.RootElement.TryGetProperty("instructions", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("stream", out _));
    }

    // --- §2: Response Deserialization ---

    [Fact]
    public async Task CompleteAsync_ValidResponse_ReturnsChatResponse()
    {
        var (provider, _) = CreateProvider(CreateCompletionResponse("Hi!"));

        var result = await provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("Hi!", result.Content);
    }

    [Fact]
    public async Task CompleteAsync_ValidResponse_MapsUsageInfo()
    {
        var (provider, _) = CreateProvider(
            CreateCompletionResponse(inputTokens: 25, outputTokens: 12, totalTokens: 37));

        var result = await provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.UsageInfo);
        Assert.Equal(25, result.UsageInfo.InputTokens);
        Assert.Equal(12, result.UsageInfo.OutputTokens);
        Assert.Equal(37, result.UsageInfo.TotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_ModelPassthrough()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(model: "anthropic/claude-sonnet-4"), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("anthropic/claude-sonnet-4", body.RootElement.GetProperty("model").GetString());
    }

    // --- §3: Streaming ---

    [Fact]
    public async Task StreamAsync_YieldsChunks()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hello"),
            DeltaEvent(" world"),
            CompletedEvent());
        var (provider, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in provider.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.Equal("Hello", chunks[0].Content);
        Assert.Equal(" world", chunks[1].Content);
    }

    [Fact]
    public async Task StreamAsync_FinalChunk_HasIsComplete()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hi"),
            CompletedEvent());
        var (provider, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in provider.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.False(chunks[0].IsComplete);
        Assert.True(chunks.Last().IsComplete);
    }

    [Fact]
    public async Task StreamAsync_FinalChunk_IncludesUsageInfo()
    {
        var response = CreateStreamResponse(
            DeltaEvent("Hi"),
            CompletedEvent(20, 10, 30));
        var (provider, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in provider.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var final = chunks.Last();
        Assert.NotNull(final.UsageInfo);
        Assert.Equal(20, final.UsageInfo.InputTokens);
        Assert.Equal(10, final.UsageInfo.OutputTokens);
        Assert.Equal(30, final.UsageInfo.TotalTokens);
    }

    [Fact]
    public async Task StreamAsync_EmptyStream_HandlesGracefully()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/event-stream")
        };
        var (provider, _) = CreateProvider(response);

        var chunks = new List<StreamedChunk>();
        await foreach (var chunk in provider.StreamAsync(CreateRequest(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        Assert.Empty(chunks);
    }

    // --- §4: Error Handling ---

    [Fact]
    public async Task CompleteAsync_Auth401_ThrowsAuthException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid key\"}")
        });

        var provider = CreateProviderWithHandler(handler);

        await Assert.ThrowsAsync<ChatAuthenticationException>(
            () => provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_RateLimit429_ThrowsRateLimitException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}")
        });

        var provider = CreateProviderWithHandler(handler);

        await Assert.ThrowsAsync<ChatRateLimitException>(
            () => provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_Timeout_ThrowsTimeoutException()
    {
        var client = new HttpClient(new TimeoutHandler())
        {
            BaseAddress = new Uri("https://openrouter.ai")
        };
        var provider = new OpenRouterChatProvider(client);

        await Assert.ThrowsAsync<ChatTimeoutException>(
            () => provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_ServerError500_ThrowsProviderException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"server error\"}")
        });

        var provider = CreateProviderWithHandler(handler);

        await Assert.ThrowsAsync<ChatProviderException>(
            () => provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_MalformedJson_ThrowsDeserializationException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "application/json")
        });

        var provider = CreateProviderWithHandler(handler);

        await Assert.ThrowsAsync<ChatDeserializationException>(
            () => provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken));
    }

    // --- §5: Request Headers ---

    [Fact]
    public async Task CompleteAsync_AuthorizationHeader_IsSet()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer test-key", handler.LastRequest!.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task CompleteAsync_AttributionHeaders_ArePresent()
    {
        var (provider, handler) = CreateProvider(CreateCompletionResponse());

        await provider.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("https://iris.qreedence.com",
            handler.LastRequest!.Headers.GetValues("HTTP-Referer").First());
        Assert.Equal("Iris",
            handler.LastRequest!.Headers.GetValues("X-OpenRouter-Title").First());
    }
}