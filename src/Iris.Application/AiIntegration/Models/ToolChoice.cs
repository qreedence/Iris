namespace Iris.Application.AiIntegration.Models;

public enum ToolChoiceMode
{
    Auto,
    None,
    Specific
}

public sealed record ToolChoice
{
    private ToolChoice(ToolChoiceMode mode, string? functionName)
    {
        Mode = mode;
        FunctionName = functionName;
    }

    public ToolChoiceMode Mode { get; }
    public string? FunctionName { get; }

    public static ToolChoice Auto { get; } = new(ToolChoiceMode.Auto, null);
    public static ToolChoice None { get; } = new(ToolChoiceMode.None, null);

    public static ToolChoice Specific(string functionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        return new ToolChoice(ToolChoiceMode.Specific, functionName);
    }
}
