using System.Text.Json;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Domain.AiIntegration;

namespace Iris.Infrastructure.AiIntegration;

public class GetCurrentTimeTool : ITool
{
    private static readonly JsonElement ParametersSchema = CreateParametersSchema();
    private readonly TimeProvider _timeProvider;

    public GetCurrentTimeTool(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ToolDefinition Definition { get; } = new(
        "get_current_time",
        "Get the current date and time in UTC.",
        ParametersSchema);

    public Task<ToolResult> ExecuteAsync(
        string argumentsJson,
        ToolContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var utcNow = _timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new { utc = utcNow.ToString("O") });

        return Task.FromResult(new ToolResult(
            payload,
            utcNow.ToString("O"),
            ToolExecutionStatus.Succeeded));
    }

    private static JsonElement CreateParametersSchema()
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");
        return document.RootElement.Clone();
    }
}
