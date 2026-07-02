using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Infrastructure.AiIntegration.Models;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iris.Infrastructure.AiIntegration;

public class OpenRouterChatProvider : IChatProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;

    public OpenRouterChatProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<StreamedChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var response = await SendStreamRequestAsync(request, ct);
        using var stream = await ReadStreamAsync(response, ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await ReadLineAsync(reader, ct)) != null)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var chunk = ParseStreamEvent(line["data: ".Length..]);
            if (chunk is not null)
                yield return chunk;
        }
    }

    private async Task<HttpResponseMessage> SendStreamRequestAsync(ChatRequest request, CancellationToken ct)
    {
        try
        {
            var content = JsonContent.Create(MapToOpenRouterRequest(request, true), options: _jsonOptions);

            var response = await _httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/api/v1/responses") { Content = content },
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            await EnsureSuccessAsync(response, ct);
            return response;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChatTimeoutException("OpenRouter streaming request timed out", ex);
        }
        catch (JsonException ex)
        {
            throw new ChatDeserializationException("Failed to serialize OpenRouter streaming request", ex);
        }
    }

    private static async Task<Stream> ReadStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChatTimeoutException("OpenRouter streaming response timed out", ex);
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChatTimeoutException("OpenRouter streaming response timed out", ex);
        }
    }

    private StreamedChunk? ParseStreamEvent(string json)
    {
        try
        {
            if (json is "[DONE]")
                return null;

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "response.output_text.delta")
            {
                var delta = doc.RootElement.GetProperty("delta").GetString();
                return new StreamedChunk(delta, false, null);
            }
            else if (type == "response.completed")
            {
                UsageInfo? usage = null;
                if (doc.RootElement.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("usage", out var u))
                {
                    usage = new UsageInfo(
                        u.GetProperty("input_tokens").GetInt32(),
                        u.GetProperty("output_tokens").GetInt32(),
                        u.GetProperty("total_tokens").GetInt32());
                }
                return new StreamedChunk(null, true, usage);
            }

            return null;
        }
        catch (JsonException ex)
        {
            throw new ChatDeserializationException("Failed to deserialize OpenRouter stream event", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new ChatDeserializationException("Failed to deserialize OpenRouter stream event", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ChatDeserializationException("Failed to deserialize OpenRouter stream event", ex);
        }
    }

    private OpenRouterRequest MapToOpenRouterRequest(ChatRequest request, bool stream = false)
    {
        return new OpenRouterRequest(
            Model: request.Model,
            Input: request.Messages
                .Select(m => new OpenRouterMessage(m.Role.ToString().ToLowerInvariant(), m.Content))
                .ToList(),
            Instructions: request.SystemPrompt,
            Temperature: request.ModelParameters?.Temperature,
            MaxOutputTokens: request.ModelParameters?.MaxOutputTokens,
            TopP: request.ModelParameters?.TopP,
            Stream: stream ? true : null
        );
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);

        throw response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                new ChatAuthenticationException($"OpenRouter authentication failed: {body}"),
            System.Net.HttpStatusCode.TooManyRequests =>
                new ChatRateLimitException($"OpenRouter rate limit exceeded: {body}"),
            System.Net.HttpStatusCode.InternalServerError =>
                new ChatProviderException($"OpenRouter server error: {body}"),
            _ => new ChatProviderException($"OpenRouter request failed ({(int)response.StatusCode}): {body}")
        };
    }
}
