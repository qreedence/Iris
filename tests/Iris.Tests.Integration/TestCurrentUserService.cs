using Iris.Application.Identity.Interfaces;

namespace Iris.Tests.Integration;

public class TestCurrentUserService : ICurrentUserService
{
    public Guid UserId { get; set; } = Guid.Empty;

    public bool IsAuthenticated => UserId != Guid.Empty;
}
