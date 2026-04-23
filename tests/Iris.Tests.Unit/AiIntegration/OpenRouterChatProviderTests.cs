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

    private static HttpResponseMessage CreateCompletionResponseWithoutUsage(string content = "Hello!")
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
            }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateEmptyOutputResponse()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "resp_123",
            output = Array.Empty<object>(),
            usage = new { input_tokens = 5, output_tokens = 0, total_tokens = 5 }
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

    // --- §1: Request Serialization ---

    [Fact]
    public async Task CompleteAsync_SendsCorrectRequestShape()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(1);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/responses");

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("test/model");
        body.RootElement.GetProperty("input")[0].GetProperty("role").GetString().Should().Be("user");
        body.RootElement.GetProperty("input")[0].GetProperty("content").GetString().Should().Be("Hello");
    }

    [Fact]
    public async Task CompleteAsync_WithModelParameters_IncludesInRequest()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(
            modelParameters: new ModelParameters(0.7f, 500, 0.9f)), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("temperature").GetSingle().Should().Be(0.7f);
        body.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(500);
        body.RootElement.GetProperty("top_p").GetSingle().Should().Be(0.9f);
    }

    [Fact]
    public async Task CompleteAsync_WithSystemPrompt_IncludesInRequest()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(systemPrompt: "You are helpful."), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("instructions").GetString().Should().Be("You are helpful.");
    }

    [Fact]
    public async Task CompleteAsync_WithNullOptionals_OmitsFromJson()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.TryGetProperty("instructions", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("max_output_tokens", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("top_p", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("stream", out _).Should().BeFalse();
    }

    // --- §2: Response Deserialization ---

    [Fact]
    public async Task CompleteAsync_ValidResponse_ReturnsChatResponse()
    {
        var (sut, _) = CreateProvider(CreateCompletionResponse("Hi!"));

        var result = await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.Content.Should().Be("Hi!");
    }

    [Fact]
    public async Task CompleteAsync_ValidResponse_MapsUsageInfo()
    {
        var (sut, _) = CreateProvider(
            CreateCompletionResponse(inputTokens: 25, outputTokens: 12, totalTokens: 37));

        var result = await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.UsageInfo.Should().NotBeNull();
        result.UsageInfo!.InputTokens.Should().Be(25);
        result.UsageInfo.OutputTokens.Should().Be(12);
        result.UsageInfo.TotalTokens.Should().Be(37);
    }

    [Fact]
    public async Task CompleteAsync_ModelPassthrough()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(model: "anthropic/claude-sonnet-4"), TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("anthropic/claude-sonnet-4");
    }

    [Fact]
    public async Task CompleteAsync_EmptyOutput_ReturnsEmptyContent()
    {
        var (sut, _) = CreateProvider(CreateEmptyOutputResponse());

        var result = await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_MissingUsage_ReturnsNullUsageInfo()
    {
        var (sut, _) = CreateProvider(CreateCompletionResponseWithoutUsage());

        var result = await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.Content.Should().Be("Hello!");
        result.UsageInfo.Should().BeNull();
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
    public async Task CompleteAsync_Auth401_ThrowsAuthException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid key\"}")
        });
        var sut = CreateProviderWithHandler(handler);

        var act = () => sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChatAuthenticationException>();
    }

    [Fact]
    public async Task CompleteAsync_RateLimit429_ThrowsRateLimitException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}")
        });
        var sut = CreateProviderWithHandler(handler);

        var act = () => sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChatRateLimitException>();
    }

    [Fact]
    public async Task CompleteAsync_Timeout_ThrowsTimeoutException()
    {
        var client = new HttpClient(new TimeoutHandler())
        {
            BaseAddress = new Uri("https://openrouter.ai")
        };
        var sut = new OpenRouterChatProvider(client);

        var act = () => sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChatTimeoutException>();
    }

    [Fact]
    public async Task CompleteAsync_ServerError500_ThrowsProviderException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"server error\"}")
        });
        var sut = CreateProviderWithHandler(handler);

        var act = () => sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChatProviderException>();
    }

    [Fact]
    public async Task CompleteAsync_MalformedJson_ThrowsDeserializationException()
    {
        var handler = new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "application/json")
        });
        var sut = CreateProviderWithHandler(handler);

        var act = () => sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChatDeserializationException>();
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

    // --- §5: Request Headers ---
    // Note: These verify that headers configured on HttpClient via DI (DependencyInjection.cs)
    // survive through to the outgoing request. The provider itself doesn't set headers —
    // the real production wiring is tested via integration tests.

    [Fact]
    public async Task CompleteAsync_AuthorizationHeader_IsSet()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.Authorization?.ToString().Should().Be("Bearer test-key");
    }

    [Fact]
    public async Task CompleteAsync_AttributionHeaders_ArePresent()
    {
        var (sut, handler) = CreateProvider(CreateCompletionResponse());

        await sut.CompleteAsync(CreateRequest(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("HTTP-Referer").First().Should().Be("https://iris.qreedence.com");
        handler.LastRequest!.Headers.GetValues("X-OpenRouter-Title").First().Should().Be("Iris");
    }
}