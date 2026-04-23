using Iris.Application.AiIntegration;
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

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var content = JsonContent.Create(MapToOpenRouterRequest(request), options: _jsonOptions);
       
        using var response = await _httpClient.PostAsync("/api/v1/responses", content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        var orResponse = JsonSerializer.Deserialize<OpenRouterResponse>(json, _jsonOptions)
            ?? throw new JsonException("Failed to deserialize OpenRouter response");

        var usage = orResponse.Usage is { } u
            ? new UsageInfo(u.InputTokens, u.OutputTokens, u.TotalTokens)
            : null;

        var text = orResponse.Output
               .Where(o => o.Type == "message")
               .SelectMany(o => o.Content ?? [])
               .Where(c => c.Type == "output_text")
               .Select(c => c.Text)
               .FirstOrDefault() ?? string.Empty;

        return new ChatResponse(text, usage);
    }

    public async IAsyncEnumerable<StreamedChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var content = JsonContent.Create(MapToOpenRouterRequest(request, true), options: _jsonOptions);

        using var response = await _httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/responses") { Content = content },
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var json = line["data: ".Length..];

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "response.output_text.delta")
            {
                var delta = doc.RootElement.GetProperty("delta").GetString();
                yield return new StreamedChunk(delta, false, null);
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
                yield return new StreamedChunk(null, true, usage);
            }
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
}