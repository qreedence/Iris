namespace Iris.Application.Identity.DTOs
{
    public record MeResponse
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
