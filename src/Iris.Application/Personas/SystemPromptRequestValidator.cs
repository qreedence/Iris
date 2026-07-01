using Iris.Application.Exceptions;

namespace Iris.Application.Personas;

public static class SystemPromptRequestValidator
{
    public static void EnsureOnlyEditableSections(SystemPromptSectionsRequest? request)
    {
        if (request?.ExtensionData is null || request.ExtensionData.Count == 0)
            return;

        var invalidSection = request.ExtensionData.Keys.First();
        var normalized = Normalize(invalidSection);

        if (normalized is "appcontext" or "guidelines")
            throw new ValidationException("AppContext and Guidelines are platform-owned system prompt sections.");

        throw new ValidationException($"Unknown system prompt section '{invalidSection}'.");
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
