using System.Text.Json;
using Iris.Application.AiIntegration.Models;
using Iris.Application.AiIntegration.Tools;
using Iris.Application.Personas;
using Iris.Domain.AiIntegration;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.AiIntegration;

public class CreatePersonaTool : ITool
{
    private static readonly JsonElement ParametersSchema = CreateParametersSchema();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly IPersonaService _personaService;

    public CreatePersonaTool(AppDbContext db, IPersonaService personaService)
    {
        _db = db;
        _personaService = personaService;
    }

    public ToolDefinition Definition { get; } = new(
        "create_persona",
        "Create a user-managed AI persona with the supplied name and role.",
        ParametersSchema);

    public string FailureMessage => "Persona creation failed.";

    public async Task<ToolResult> ExecuteAsync(
        string argumentsJson,
        ToolContext context,
        CancellationToken ct = default)
    {
        CreatePersonaArguments? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<CreatePersonaArguments>(argumentsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return Failure("Tool arguments were not valid for create_persona.");
        }

        if (string.IsNullOrWhiteSpace(arguments?.Name))
            return Failure("Persona name is required.");

        if (string.IsNullOrWhiteSpace(arguments.Role))
            return Failure("Persona role is required.");

        if (arguments.Name.Trim().Length > 100)
            return Failure("Persona name must be 100 characters or fewer.");

        if (arguments.Role.Trim().Length > 200)
            return Failure("Persona role must be 200 characters or fewer.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var priorExecution = await _db.PersonaCreationToolExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                execution => execution.ConversationId == context.ConversationId
                    && execution.ToolCallId == context.ToolCallId,
                ct);

        PersonaDto persona;
        if (priorExecution is not null)
        {
            persona = await _personaService.GetByIdAsync(priorExecution.PersonaId, ct);
        }
        else
        {
            persona = await _personaService.CreateAsync(
                context.UserId,
                new CreatePersonaRequest(arguments.Name.Trim(), Role: arguments.Role.Trim()),
                ct);

            _db.PersonaCreationToolExecutions.Add(new PersonaCreationToolExecution
            {
                ConversationId = context.ConversationId,
                ToolCallId = context.ToolCallId,
                PersonaId = persona.Id,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);

        var payload = JsonSerializer.Serialize(
            new { id = persona.Id, name = persona.Name, role = persona.Role },
            JsonOptions);

        return new ToolResult(payload, $"Created {persona.Name}", ToolExecutionStatus.Succeeded);
    }

    private static ToolResult Failure(string message) => new(
        JsonSerializer.Serialize(new { error = message }, JsonOptions),
        message,
        ToolExecutionStatus.Failed);

    private static JsonElement CreateParametersSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The persona's display name." },
                "role": { "type": "string", "description": "A concise description of how the persona helps the user." }
              },
              "required": ["name", "role"],
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed record CreatePersonaArguments(string? Name, string? Role);
}
