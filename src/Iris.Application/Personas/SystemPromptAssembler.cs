using System.Text;

namespace Iris.Application.Personas;

public class SystemPromptAssembler : ISystemPromptAssembler
{
    private readonly IGlobalSystemPromptProvider _globalSystemPromptProvider;

    public SystemPromptAssembler(IGlobalSystemPromptProvider globalSystemPromptProvider)
    {
        _globalSystemPromptProvider = globalSystemPromptProvider;
    }

    public async Task<string?> BuildAsync(SystemPromptDto systemPrompt, CancellationToken ct = default)
    {
        var globalSections = await _globalSystemPromptProvider.GetAsync(ct);
        var builder = new StringBuilder();

        AppendSection(builder, "app_context", globalSections.AppContext);
        AppendSection(builder, "guidelines", globalSections.Guidelines);

        foreach (var definition in SystemPromptSections.All)
        {
            AppendSection(builder, definition.TagName, definition.GetFromDto(systemPrompt));
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
