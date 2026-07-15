using Iris.Application.AiIntegration;
using Iris.Application.AiIntegration.Exceptions;
using Iris.Application.AiIntegration.Models;
using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Content;
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

        var blockIndexes = new Dictionary<ContentBlockType, int>();
        var toolCalls = new ToolCallAccumulator();
        var completed = false;

        string? line;
        while ((line = await ReadLineAsync(reader, ct)) != null)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var chunks = ParseStreamEvent(line["data: ".Length..], toolCalls);
            foreach (var chunk in chunks)
            {
                completed |= chunk.IsComplete;
                yield return AssignBlockIndex(chunk, blockIndexes);
            }
        }

        if (!completed)
            throw new ChatProviderException("OpenRouter stream ended without response.completed.");
    }

    private async Task<HttpResponseMessage> SendStreamRequestAsync(ChatRequest request, CancellationToken ct)
    {
        try
        {
            var content = JsonContent.Create(MapToOpenRouterRequest(request), options: _jsonOptions);
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/responses")
            {
                Content = content,
            };

            var response = await _httpClient.SendAsync(
                requestMessage,
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

    private static StreamedChunk AssignBlockIndex(
        StreamedChunk chunk,
        Dictionary<ContentBlockType, int> blockIndexes)
    {
        if (chunk.IsComplete)
            return chunk;

        if (!blockIndexes.TryGetValue(chunk.BlockType, out var blockIndex))
        {
            blockIndex = blockIndexes.Count;
            blockIndexes[chunk.BlockType] = blockIndex;
        }

        return chunk with { BlockIndex = blockIndex };
    }

    private static IReadOnlyList<StreamedChunk> ParseStreamEvent(
        string json,
        ToolCallAccumulator toolCalls)
    {
        try
        {
            if (json is "[DONE]")
                return [];

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var typeProperty)
                ? typeProperty.GetString()
                : null;

            if (type is "response.failed" or "response.error" or "response.incomplete" or "error")
                throw CreateStreamFailure(doc.RootElement, type);

            if (type == "response.output_text.delta")
            {
                var delta = doc.RootElement.GetProperty("delta").GetString();
                return [new StreamedChunk(delta, false, null)];
            }

            if (type == "response.reasoning.delta")
            {
                var delta = doc.RootElement.GetProperty("delta").GetString();
                return [new StreamedChunk(delta, false, null, ContentBlockType.Thinking)];
            }

            if (type == "response.completed")
            {
                CaptureCompletedToolCalls(doc.RootElement, toolCalls);

                UsageInfo? usage = null;
                if (doc.RootElement.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("usage", out var u))
                {
                    usage = new UsageInfo(
                        u.GetProperty("input_tokens").GetInt32(),
                        u.GetProperty("output_tokens").GetInt32(),
                        u.GetProperty("total_tokens").GetInt32());
                }
                var completedToolCalls = toolCalls.Build();
                var finishReason = completedToolCalls.Count > 0
                    ? FinishReason.ToolCalls
                    : FinishReason.Stop;

                return [new StreamedChunk(
                    null,
                    true,
                    usage,
                    ToolCalls: completedToolCalls.Count == 0 ? null : completedToolCalls,
                    FinishReason: finishReason)];
            }

            if (type == "response.output_item.added")
            {
                CaptureToolCallStart(doc.RootElement, toolCalls);
                return [];
            }

            if (type == "response.function_call_arguments.delta")
            {
                CaptureToolCallDelta(doc.RootElement, toolCalls);
                return [];
            }

            if (type == "response.function_call_arguments.done")
            {
                CaptureToolCallDone(doc.RootElement, toolCalls);
                return [];
            }

            if (TryParseReasoningDetails(doc.RootElement, out var reasoningChunks))
                return reasoningChunks;

            if (TryParseLegacyReasoning(doc.RootElement, out var legacyReasoningChunk))
                return [legacyReasoningChunk];

            return [];
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

    private static ChatProviderException CreateStreamFailure(JsonElement root, string eventType)
    {
        var errorContainer = root;
        if (root.TryGetProperty("response", out var response))
            errorContainer = response;

        var message = errorContainer.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var messageProperty)
            ? messageProperty.GetString()
            : null;
        var errorType = errorContainer.TryGetProperty("error_type", out var errorTypeProperty)
            ? errorTypeProperty.GetString()
            : null;

        return new ChatProviderException(
            $"OpenRouter {eventType}{(string.IsNullOrWhiteSpace(errorType) ? string.Empty : $" ({errorType})")}: "
            + (string.IsNullOrWhiteSpace(message) ? "The response did not complete." : message));
    }

    private static void CaptureToolCallStart(JsonElement root, ToolCallAccumulator toolCalls)
    {
        if (!root.TryGetProperty("item", out var item)
            || !item.TryGetProperty("type", out var itemType)
            || itemType.GetString() != "function_call")
        {
            return;
        }

        var outputIndex = root.GetProperty("output_index").GetInt32();
        var providerItemId = item.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Function-call item is missing id.");
        var callId = item.GetProperty("call_id").GetString()
            ?? throw new InvalidOperationException("Function-call item is missing call_id.");
        var name = item.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Function-call item is missing name.");
        var arguments = item.TryGetProperty("arguments", out var argumentsProperty)
            ? argumentsProperty.GetString() ?? string.Empty
            : string.Empty;

        toolCalls.Start(outputIndex, providerItemId, callId, name, arguments);
    }

    private static void CaptureToolCallDelta(JsonElement root, ToolCallAccumulator toolCalls)
    {
        var outputIndex = root.GetProperty("output_index").GetInt32();
        var delta = root.GetProperty("delta").GetString()
            ?? throw new InvalidOperationException("Function-call argument delta is null.");
        var providerItemId = root.TryGetProperty("item_id", out var itemId)
            ? itemId.GetString()
            : null;

        toolCalls.Append(outputIndex, providerItemId, delta);
    }

    private static void CaptureToolCallDone(JsonElement root, ToolCallAccumulator toolCalls)
    {
        var outputIndex = root.GetProperty("output_index").GetInt32();
        var arguments = root.GetProperty("arguments").GetString()
            ?? throw new InvalidOperationException("Completed function-call arguments are null.");
        var providerItemId = root.TryGetProperty("item_id", out var itemId)
            ? itemId.GetString()
            : null;

        toolCalls.Complete(outputIndex, providerItemId, arguments);
    }

    private static void CaptureCompletedToolCalls(JsonElement root, ToolCallAccumulator toolCalls)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var outputIndex = 0;
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var itemType)
                && itemType.GetString() == "function_call")
            {
                toolCalls.AddOrReplace(
                    outputIndex,
                    item.GetProperty("id").GetString()
                        ?? throw new InvalidOperationException("Function-call item is missing id."),
                    item.GetProperty("call_id").GetString()
                        ?? throw new InvalidOperationException("Function-call item is missing call_id."),
                    item.GetProperty("name").GetString()
                        ?? throw new InvalidOperationException("Function-call item is missing name."),
                    item.GetProperty("arguments").GetString()
                        ?? throw new InvalidOperationException("Function-call item is missing arguments."));
            }

            outputIndex++;
        }
    }

    private static bool TryParseLegacyReasoning(JsonElement root, out StreamedChunk chunk)
    {
        chunk = default!;

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            if (!delta.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.String)
                continue;

            chunk = new StreamedChunk(
                reasoning.GetString(),
                false,
                null,
                ContentBlockType.Thinking);
            return true;
        }

        return false;
    }

    private static bool TryParseReasoningDetails(JsonElement root, out IReadOnlyList<StreamedChunk> chunks)
    {
        var parsedChunks = new List<StreamedChunk>();

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta))
                    continue;

                if (!delta.TryGetProperty("reasoning_details", out var reasoningDetails) ||
                    reasoningDetails.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var detail in reasoningDetails.EnumerateArray())
                    parsedChunks.Add(MapReasoningDetail(detail));
            }
        }

        chunks = parsedChunks;
        return parsedChunks.Count > 0;
    }

    private static StreamedChunk MapReasoningDetail(JsonElement detail)
    {
        var content = TryGetString(detail, "text")
            ?? TryGetString(detail, "summary")
            ?? string.Empty;

        return new StreamedChunk(
            content,
            false,
            null,
            ContentBlockType.Thinking,
            ProviderMetadata: [ToDictionary(detail)]);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private OpenRouterRequest MapToOpenRouterRequest(ChatRequest request)
    {
        return new OpenRouterRequest(
            Model: request.Model,
            Input: request.Messages
                .SelectMany(MapToOpenRouterInput)
                .ToList(),
            Instructions: request.SystemPrompt,
            Temperature: request.ModelParameters?.Temperature,
            MaxOutputTokens: request.ModelParameters?.MaxOutputTokens,
            TopP: request.ModelParameters?.TopP,
            Stream: true,
            Tools: request.ToolOptions?.Tools
                .Select(tool => new OpenRouterTool(
                    "function",
                    tool.Name,
                    tool.Description,
                    tool.ParametersSchema))
                .ToList(),
            ToolChoice: MapToolChoice(request.ToolOptions?.ToolChoice)
        );
    }

    private static IEnumerable<object> MapToOpenRouterInput(ChatMessage message)
    {
        var content = message.VisibleText;
        var reasoningDetails = BuildReasoningDetails(message.ContentBlocks);
        var reasoning = reasoningDetails is null ? BuildReasoningText(message.ContentBlocks) : null;

        if (!string.IsNullOrEmpty(content) || reasoningDetails is not null || reasoning is not null)
        {
            yield return new OpenRouterMessage(
                message.Role.ToString().ToLowerInvariant(),
                content,
                reasoningDetails,
                reasoning);
        }

        foreach (var block in message.ContentBlocks.Where(block => block.Type == ContentBlockType.ToolUse))
        {
            yield return new OpenRouterFunctionCall(
                "function_call",
                GetProviderItemId(block),
                block.ToolCallId
                    ?? throw new InvalidOperationException("Tool-use block is missing toolCallId."),
                block.Name
                    ?? throw new InvalidOperationException("Tool-use block is missing name."),
                block.ArgumentsJson
                    ?? throw new InvalidOperationException("Tool-use block is missing argumentsJson."));
        }

        foreach (var block in message.ContentBlocks.Where(block => block.Type == ContentBlockType.ToolResult))
        {
            yield return new OpenRouterFunctionCallOutput(
                "function_call_output",
                block.ToolCallId
                    ?? throw new InvalidOperationException("Tool-result block is missing toolCallId."),
                message.ToolResultContent
                    ?? throw new InvalidOperationException("Tool-result message is missing resolved payload content."));
        }
    }

    private static object? MapToolChoice(ToolChoice? toolChoice)
    {
        return toolChoice?.Mode switch
        {
            null => null,
            ToolChoiceMode.Auto => "auto",
            ToolChoiceMode.None => "none",
            ToolChoiceMode.Specific => new OpenRouterForcedToolChoice(
                "function",
                toolChoice.FunctionName
                    ?? throw new InvalidOperationException("Specific tool choice is missing a function name.")),
            _ => throw new InvalidOperationException($"Unsupported tool choice mode '{toolChoice.Mode}'.")
        };
    }

    private static string GetProviderItemId(MessageContentBlock block)
    {
        if (block.ProviderMetadata is not null)
        {
            foreach (var metadata in block.ProviderMetadata)
            {
                if (metadata.TryGetValue("item_id", out var value)
                    && TryGetStringValue(value, out var itemId)
                    && !string.IsNullOrWhiteSpace(itemId))
                {
                    return itemId;
                }
            }
        }

        return block.ToolCallId
            ?? throw new InvalidOperationException("Tool-use block is missing provider item ID and toolCallId.");
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>>? BuildReasoningDetails(IReadOnlyList<MessageContentBlock> blocks)
    {
        var details = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var block in blocks.Where(block => block.Type == ContentBlockType.Thinking))
        {
            if (block.ProviderMetadata is not { } metadataItems)
                continue;

            foreach (var metadata in metadataItems)
                AddReasoningMetadata(metadata, details);
        }

        return details.Count == 0
            ? null
            : details;
    }

    private static void AddReasoningMetadata(
        IReadOnlyDictionary<string, object?> metadata,
        List<IReadOnlyDictionary<string, object?>> details)
    {
        if (metadata.TryGetValue("reasoning_details", out var nestedDetails) &&
            TryAddNestedReasoningDetails(nestedDetails, details))
        {
            return;
        }

        if (metadata.TryGetValue("type", out var type) &&
            TryGetStringValue(type, out var typeString) &&
            typeString.StartsWith("reasoning.", StringComparison.Ordinal))
        {
            details.Add(NormalizeMetadata(metadata));
        }
    }

    private static bool TryAddNestedReasoningDetails(
        object? nestedDetails,
        List<IReadOnlyDictionary<string, object?>> details)
    {
        switch (nestedDetails)
        {
            case IEnumerable<IReadOnlyDictionary<string, object?>> nestedItems:
                foreach (var item in nestedItems)
                    AddReasoningMetadata(item, details);
                return true;

            case JsonElement { ValueKind: JsonValueKind.Array } nestedArray:
                foreach (var item in nestedArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        AddReasoningMetadata(ToDictionary(item), details);
                }
                return true;

            default:
                return false;
        }
    }

    private static bool TryGetStringValue(object? value, out string result)
    {
        switch (value)
        {
            case string stringValue:
                result = stringValue;
                return true;

            case JsonElement { ValueKind: JsonValueKind.String } jsonValue:
                result = jsonValue.GetString() ?? string.Empty;
                return true;

            default:
                result = string.Empty;
                return false;
        }
    }

    private static IReadOnlyDictionary<string, object?> NormalizeMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        return metadata.ToDictionary(
            item => item.Key,
            item => NormalizeMetadataValue(item.Value));
    }

    private static object? NormalizeMetadataValue(object? value)
    {
        return value switch
        {
            JsonElement element => ToPlainValue(element),
            IReadOnlyDictionary<string, object?> dictionary => NormalizeMetadata(dictionary),
            IEnumerable<IReadOnlyDictionary<string, object?>> dictionaries => dictionaries
                .Select(NormalizeMetadata)
                .ToList(),
            _ => value
        };
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(JsonElement element)
    {
        return element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ToPlainValue(property.Value));
    }

    private static object? ToPlainValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ToPlainValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static string? BuildReasoningText(IReadOnlyList<MessageContentBlock> blocks)
    {
        var reasoningText = string.Concat(blocks
            .Where(block => block.Type == ContentBlockType.Thinking)
            .Select(block => block.Content));

        return string.IsNullOrEmpty(reasoningText) ? null : reasoningText;
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
