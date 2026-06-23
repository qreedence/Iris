using Iris.Application.Identity.Interfaces;

namespace Iris.Api.Authentication
{
    public class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public Guid? OverrideUserId { get; set; }

        public Guid UserId
            => OverrideUserId
                ?? _httpContextAccessor.HttpContext?.User.GetUserId()
                ?? Guid.Empty;

        public bool IsAuthenticated
            => _httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;
    }
}
