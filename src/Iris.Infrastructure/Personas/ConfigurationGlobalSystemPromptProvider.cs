using Iris.Application.Personas;
using Microsoft.Extensions.Options;

namespace Iris.Infrastructure.Personas;

public class ConfigurationGlobalSystemPromptProvider : IGlobalSystemPromptProvider
{
    private readonly IOptionsMonitor<IrisSystemPromptOptions> _options;

    public ConfigurationGlobalSystemPromptProvider(IOptionsMonitor<IrisSystemPromptOptions> options)
    {
        _options = options;
    }

    public Task<GlobalSystemPromptSections> GetAsync(CancellationToken ct = default)
    {
        var options = _options.CurrentValue;

        return Task.FromResult(new GlobalSystemPromptSections(
            options.AppContext,
            options.Guidelines));
    }
}
