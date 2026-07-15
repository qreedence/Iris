using System.Text;
using Iris.Application.AiIntegration.Models;

namespace Iris.Infrastructure.AiIntegration;

internal sealed class ToolCallAccumulator
{
    private readonly SortedDictionary<int, MutableToolCall> _calls = [];

    public void Start(
        int outputIndex,
        string providerItemId,
        string callId,
        string name,
        string arguments)
    {
        if (_calls.ContainsKey(outputIndex))
            throw new InvalidOperationException($"Duplicate function-call output index {outputIndex}.");

        _calls[outputIndex] = new MutableToolCall(providerItemId, callId, name, arguments);
    }

    public void Append(int outputIndex, string? providerItemId, string delta)
    {
        var call = GetExisting(outputIndex);
        call.EnsureProviderItemId(providerItemId);
        call.Arguments.Append(delta);
    }

    public void Complete(int outputIndex, string? providerItemId, string arguments)
    {
        var call = GetExisting(outputIndex);
        call.EnsureProviderItemId(providerItemId);
        call.Arguments.Clear();
        call.Arguments.Append(arguments);
    }

    public void AddOrReplace(
        int outputIndex,
        string providerItemId,
        string callId,
        string name,
        string arguments)
    {
        if (_calls.TryGetValue(outputIndex, out var existing))
        {
            existing.EnsureIdentity(providerItemId, callId, name);
            existing.Arguments.Clear();
            existing.Arguments.Append(arguments);
            return;
        }

        _calls[outputIndex] = new MutableToolCall(providerItemId, callId, name, arguments);
    }

    public IReadOnlyList<ToolCall> Build() => _calls.Values
        .Select(call => new ToolCall(
            call.CallId,
            call.Name,
            call.Arguments.ToString(),
            call.ProviderItemId))
        .ToList();

    private MutableToolCall GetExisting(int outputIndex) =>
        _calls.TryGetValue(outputIndex, out var call)
            ? call
            : throw new InvalidOperationException(
                $"Function-call arguments arrived before output item {outputIndex} was added.");

    private sealed class MutableToolCall(
        string providerItemId,
        string callId,
        string name,
        string arguments)
    {
        public string ProviderItemId { get; } = providerItemId;
        public string CallId { get; } = callId;
        public string Name { get; } = name;
        public StringBuilder Arguments { get; } = new(arguments);

        public void EnsureProviderItemId(string? itemId)
        {
            if (itemId is not null && itemId != ProviderItemId)
                throw new InvalidOperationException(
                    $"Function-call item ID changed from '{ProviderItemId}' to '{itemId}'.");
        }

        public void EnsureIdentity(string itemId, string currentCallId, string currentName)
        {
            if (itemId != ProviderItemId || currentCallId != CallId || currentName != Name)
                throw new InvalidOperationException("Completed function-call identity did not match its streamed item.");
        }
    }
}
