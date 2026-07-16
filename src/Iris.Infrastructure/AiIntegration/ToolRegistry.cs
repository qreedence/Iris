using System.Text.Json;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Personas;
using Iris.Application.Exceptions;
using Microsoft.Extensions.Logging;

namespace Iris.Infrastructure.AiIntegration;

public class ToolRegistry : IToolRegistry
{
    private readonly IPersonaService _personaService;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(
        IPersonaService personaService,
        IEnumerable<ITool> tools,
        ILogger<ToolRegistry> logger)
    {
        _personaService = personaService;
        _tools = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    public async Task<IReadOnlyList<ToolDefinition>> GetToolsForPersonaAsync(
        Guid personaId,
        CancellationToken ct = default)
    {
        var persona = await _personaService.GetByIdAsync(personaId, ct);

        if (persona.Kind != PersonaKind.System)
            return [];

        return _tools.Values
            .Select(tool => tool.Definition)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using var arguments = JsonDocument.Parse(toolCall.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                return Failure("Tool arguments must be a JSON object.");
        }
        catch (JsonException)
        {
            return Failure("Tool arguments were not valid JSON.");
        }

        var enabledTools = await GetToolsForPersonaAsync(context.PersonaId, ct);
        if (!enabledTools.Any(tool => tool.Name == toolCall.FunctionName)
            || !_tools.TryGetValue(toolCall.FunctionName, out var tool))
        {
            return Failure($"Unknown or unavailable tool '{toolCall.FunctionName}'.");
        }

        try
        {
            return await tool.ExecuteAsync(toolCall.ArgumentsJson, context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ValidationException ex)
        {
            _logger.LogInformation(
                "Tool {ToolName} rejected validation for conversation {ConversationId}: {ValidationMessage}",
                toolCall.FunctionName,
                context.ConversationId,
                ex.Message);

            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Tool {ToolName} failed for conversation {ConversationId}",
                toolCall.FunctionName,
                context.ConversationId);

            return Failure(tool.FailureMessage);
        }
    }

    private static ToolResult Failure(string message)
    {
        return new ToolResult(
            JsonSerializer.Serialize(new { error = message }),
            message,
            ToolExecutionStatus.Failed);
    }
}
