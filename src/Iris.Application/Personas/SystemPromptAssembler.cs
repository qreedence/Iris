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
        AppendSection(builder, "identity", systemPrompt.Identity);
        AppendSection(builder, "voice", systemPrompt.Voice);
        AppendSection(builder, "role", systemPrompt.Role);
        AppendSection(builder, "relationship", systemPrompt.Relationship);
        AppendSection(builder, "tool_instructions", systemPrompt.ToolInstructions);

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
