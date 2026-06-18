namespace Iris.Application.Identity.DTOs
{
    public record MeResponse
    {
        public Guid UserId { get; init; }
        public string Email { get; init; } = string.Empty;
    }
}
