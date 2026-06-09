namespace Iris.Infrastructure.Identity
{
    public class JwtOptions
    {
        public const string SectionName = "Jwt";
        public string Secret { get; init; } = string.Empty;
        public int AccessTokenExpirationMinutes { get; init; } = 15;
        public int RefreshTokenExpirationDays { get; init; } = 14;
    }
}
