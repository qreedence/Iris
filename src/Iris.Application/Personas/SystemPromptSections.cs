using Iris.Domain.Personas;

namespace Iris.Application.Personas;

public sealed record SystemPromptSectionDefinition(
    SystemPromptSection Section,
    string TagName,
    Func<SystemPrompt, string?> GetFromEntity,
    Action<SystemPrompt, string?> SetOnEntity,
    Func<SystemPromptDto, string?> GetFromDto,
    Func<SystemPromptSectionsRequest, string?> GetFromRequest);

/// <summary>
/// THE single registry of persona-owned system prompt sections. Adding a 6th section
/// means adding an entry here, plus the corresponding members on
/// <see cref="Domain.Personas.SystemPrompt"/>, <see cref="SystemPromptDto"/>,
/// <see cref="SystemPromptSectionsRequest"/>, the <see cref="SystemPromptSection"/>
/// enum, and the EF configuration for the new column — nothing else should need to
/// enumerate the section list by hand.
/// </summary>
public static class SystemPromptSections
{
    public static IReadOnlyList<SystemPromptSectionDefinition> All { get; } =
    [
        new(
            SystemPromptSection.Identity,
            "identity",
            p => p.Identity,
            (p, v) => p.Identity = v,
            dto => dto.Identity,
            req => req.Identity),
        new(
            SystemPromptSection.Voice,
            "voice",
            p => p.Voice,
            (p, v) => p.Voice = v,
            dto => dto.Voice,
            req => req.Voice),
        new(
            SystemPromptSection.Role,
            "role",
            p => p.Role,
            (p, v) => p.Role = v,
            dto => dto.Role,
            req => req.Role),
        new(
            SystemPromptSection.Relationship,
            "relationship",
            p => p.Relationship,
            (p, v) => p.Relationship = v,
            dto => dto.Relationship,
            req => req.Relationship),
        new(
            SystemPromptSection.ToolInstructions,
            "tool_instructions",
            p => p.ToolInstructions,
            (p, v) => p.ToolInstructions = v,
            dto => dto.ToolInstructions,
            req => req.ToolInstructions),
    ];
}
