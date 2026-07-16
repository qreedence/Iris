using System.Text;
using Iris.Domain.Personas;

namespace Iris.Application.Personas;

public class SystemPromptAssembler : ISystemPromptAssembler
{
    private readonly IGlobalSystemPromptProvider _globalSystemPromptProvider;

    public SystemPromptAssembler(IGlobalSystemPromptProvider globalSystemPromptProvider)
    {
        _globalSystemPromptProvider = globalSystemPromptProvider;
    }

    public Task<string?> BuildAsync(SystemPromptDto systemPrompt, CancellationToken ct = default)
    {
        return BuildCoreAsync(systemPrompt, isSystemPersona: false, ct);
    }

    public Task<string?> BuildAsync(PersonaDto persona, CancellationToken ct = default)
    {
        return BuildCoreAsync(persona.SystemPrompt, persona.Kind == PersonaKind.System, ct);
    }

    private async Task<string?> BuildCoreAsync(
        SystemPromptDto systemPrompt,
        bool isSystemPersona,
        CancellationToken ct)
    {
        var globalSections = await _globalSystemPromptProvider.GetAsync(ct);
        var builder = new StringBuilder();

        AppendSection(builder, "app_context", globalSections.AppContext);
        AppendSection(builder, "guidelines", globalSections.Guidelines);

        if (isSystemPersona)
        {
            AppendSection(builder, "orchestrator", globalSections.Orchestrator);
        }
        else
        {
            foreach (var definition in SystemPromptSections.All)
            {
                AppendSection(builder, definition.TagName, definition.GetFromDto(systemPrompt));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string tagName, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        if (builder.Length > 0)
            builder.AppendLine().AppendLine();

        builder
            .Append('<').Append(tagName).AppendLine(">")
            .AppendLine(content.Trim())
            .Append("</").Append(tagName).Append('>');
    }
}
