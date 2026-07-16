namespace Iris.Application.Personas;

public interface IOrchestratorProvisioner
{
    Task<OrchestratorProvisioningResult> EnsureProvisionedAsync(
        Guid userId,
        CancellationToken ct = default);
}
