using Iris.Application.Conversations;
using Iris.Application.Identity.Interfaces;
using Iris.Application.Personas;
using Iris.Domain.Conversations.Events;
using Iris.Domain.Personas;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Iris.Infrastructure.Personas;

public class OrchestratorProvisioner : IOrchestratorProvisioner
{
    private const string OrchestratorName = "Iris";
    private const string OrchestratorRole = "orchestrator";
    private const string InitialConversationTitle = "Build your AI squad";

    private readonly AppDbContext _db;
    private readonly IConversationEventRecorder _eventRecorder;
    private readonly ICurrentUserService _currentUser;

    public OrchestratorProvisioner(
        AppDbContext db,
        IConversationEventRecorder eventRecorder,
        ICurrentUserService currentUser)
    {
        _db = db;
        _eventRecorder = eventRecorder;
        _currentUser = currentUser;
    }

    public async Task<OrchestratorProvisioningResult> EnsureProvisionedAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId cannot be empty.", nameof(userId));

        var priorOverride = _currentUser.OverrideUserId;
        _currentUser.OverrideUserId = userId;

        try
        {
            try
            {
                return await EnsureOnceAsync(userId, ct);
            }
            catch (DbUpdateException ex) when (IsSystemPersonaRace(ex))
            {
                // A concurrent first login won the unique-index race. The failed
                // transaction has rolled back, so clear its tracked insert and heal
                // against the winner instead of surfacing a first-login 500.
                _db.ChangeTracker.Clear();
                return await EnsureOnceAsync(userId, ct);
            }
        }
        finally
        {
            _currentUser.OverrideUserId = priorOverride;
        }
    }

    private async Task<OrchestratorProvisioningResult> EnsureOnceAsync(
        Guid userId,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var orchestrator = await _db.Personas
            .FirstOrDefaultAsync(persona => persona.Kind == PersonaKind.System, ct);

        if (orchestrator is null)
        {
            var now = DateTimeOffset.UtcNow;
            orchestrator = new Persona
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = OrchestratorName,
                Kind = PersonaKind.System,
                Role = OrchestratorRole,
                CreatedAt = now,
                UpdatedAt = now
            };

            _db.Personas.Add(orchestrator);
            await _db.SaveChangesAsync(ct);
        }

        var conversationId = await _db.ConversationReadModels
            .Where(conversation => conversation.PersonaId == orchestrator.Id)
            .OrderBy(conversation => conversation.CreatedAt)
            .Select(conversation => conversation.Id)
            .FirstOrDefaultAsync(ct);

        if (conversationId == Guid.Empty)
        {
            conversationId = Guid.NewGuid();
            await _eventRecorder.RecordAsync(
                conversationId,
                [new ConversationCreated(
                    conversationId,
                    userId,
                    orchestrator.Id,
                    InitialConversationTitle)],
                ct);
        }

        await transaction.CommitAsync(ct);
        return new OrchestratorProvisioningResult(orchestrator.Id, conversationId);
    }

    private static bool IsSystemPersonaRace(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_personas_UserId_System"
        };
    }
}
