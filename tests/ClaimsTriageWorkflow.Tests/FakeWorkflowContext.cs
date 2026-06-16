using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace ClaimsTriageWorkflow.Tests;

/// <summary>
/// In-memory IWorkflowContext for unit testing executors without running a full workflow.
/// State updates are applied immediately; unsupported methods throw NotImplementedException.
/// </summary>
internal sealed class FakeWorkflowContext : IWorkflowContext
{
    private readonly Dictionary<string, JsonElement> _state = new();

    public bool ConcurrentRunsEnabled => false;
    public IReadOnlyDictionary<string, string> TraceContext => new Dictionary<string, string>();

    // Pre-seed a value so executors that read state can find it.
    public void SetState<T>(string key, T value)
        => _state[key] = JsonSerializer.SerializeToElement(value);

    public ValueTask QueueStateUpdateAsync<T>(string key, T? value, CancellationToken ct = default)
    {
        _state[key] = JsonSerializer.SerializeToElement(value);
        return ValueTask.CompletedTask;
    }

    public ValueTask QueueStateUpdateAsync<T>(string key, T? value, string? scopeName = null, CancellationToken ct = default)
        => QueueStateUpdateAsync(key, value, ct);

    public ValueTask<T?> ReadStateAsync<T>(string key, CancellationToken ct = default)
    {
        if (_state.TryGetValue(key, out var element))
            return ValueTask.FromResult(element.Deserialize<T>());
        return ValueTask.FromResult(default(T?));
    }

    public ValueTask<T?> ReadStateAsync<T>(string key, string? scopeName = null, CancellationToken ct = default)
        => ReadStateAsync<T>(key, ct);

    public ValueTask<T> ReadOrInitStateAsync<T>(string key, Func<T> initialStateFactory, CancellationToken ct = default)
    {
        if (_state.TryGetValue(key, out var element))
            return ValueTask.FromResult(element.Deserialize<T>()!);
        var v = initialStateFactory();
        _state[key] = JsonSerializer.SerializeToElement(v);
        return ValueTask.FromResult(v);
    }

    public ValueTask<T> ReadOrInitStateAsync<T>(string key, Func<T> initialStateFactory, string? scopeName = null, CancellationToken ct = default)
        => ReadOrInitStateAsync(key, initialStateFactory, ct);

    public ValueTask<HashSet<string>> ReadStateKeysAsync(string? scopeName = null, CancellationToken ct = default)
        => ValueTask.FromResult(new HashSet<string>(_state.Keys));

    public ValueTask QueueClearScopeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask QueueClearScopeAsync(string? scopeName = null, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask AddEventAsync(WorkflowEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask SendMessageAsync(object message, string? targetId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask YieldOutputAsync(object output, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask RequestHaltAsync() => ValueTask.CompletedTask;
}
