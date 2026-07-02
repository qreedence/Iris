namespace Iris.Application.Personas;

public static class SystemPromptSectionParser
{
    public static bool TryParse(string value, out SystemPromptSection section)
    {
        var normalized = Normalize(value);
        var match = SystemPromptSections.All.FirstOrDefault(d => Normalize(d.TagName) == normalized);

        if (match is null)
        {
            section = default;
            return false;
        }

        section = match.Section;
        return true;
    }

    internal static string Normalize(string value)
    {
        return value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
