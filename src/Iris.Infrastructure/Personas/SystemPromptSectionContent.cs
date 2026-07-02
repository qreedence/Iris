namespace Iris.Infrastructure.Personas;

internal static class SystemPromptSectionContent
{
    internal static string? Normalize(string? content)
    {
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }
}
