namespace Iris.Application.Identity.Interfaces
{
    public interface ICurrentUserService
    {
        Guid UserId { get; }
        Guid? OverrideUserId { get; set; }
        bool IsAuthenticated { get; }
    }
}
