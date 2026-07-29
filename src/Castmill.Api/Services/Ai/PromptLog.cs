namespace Castmill.Api.Services.Ai;

public sealed record PromptLogEntry(
    DateTimeOffset At,
    Guid UserId,
    string Kind,
    string ModelAlias,
    string PromptExcerpt,
    string ResponseExcerpt,
    bool Success,
    long DurationMs);

public interface IPromptLog
{
    void Record(PromptLogEntry entry);
    IReadOnlyList<PromptLogEntry> ForUser(Guid userId);
}

/// <summary>
/// In-memory ring buffer for AI support/debugging (G7). Excerpts are capped and
/// never contain credentials — prompts are built from transcript + brief only.
/// Deliberately not persisted: it's a transparency window, not an audit store.
/// </summary>
public sealed class PromptLog : IPromptLog
{
    private const int Capacity = 200;
    public const int ExcerptLength = 4000;

    private readonly Lock _lock = new();
    private readonly Queue<PromptLogEntry> _entries = new(Capacity);

    public void Record(PromptLogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
            }
            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<PromptLogEntry> ForUser(Guid userId)
    {
        lock (_lock)
        {
            return [.. _entries.Where(e => e.UserId == userId)];
        }
    }
}
