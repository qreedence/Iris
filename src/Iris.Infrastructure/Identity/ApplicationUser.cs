using Microsoft.AspNetCore.Identity;

namespace Iris.Infrastructure.Identity
{
    public class ApplicationUser : IdentityUser<Guid>
    {
        public string? DisplayName { get; set; } = null;
        public string? AvatarUrl { get; set; } = null;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
