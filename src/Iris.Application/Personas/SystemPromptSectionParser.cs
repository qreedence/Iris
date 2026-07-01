namespace Iris.Application.Personas;

public static class SystemPromptSectionParser
{
    public static bool TryParse(string value, out SystemPromptSection section)
    {
        switch (Normalize(value))
        {
            case "identity":
                section = SystemPromptSection.Identity;
                return true;
            case "voice":
                section = SystemPromptSection.Voice;
                return true;
            case "role":
                section = SystemPromptSection.Role;
                return true;
            case "relationship":
                section = SystemPromptSection.Relationship;
                return true;
            case "toolinstructions":
                section = SystemPromptSection.ToolInstructions;
                return true;
            default:
                section = default;
                return false;
        }
    }

    private static string Normalize(string value)
    {
        return value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
