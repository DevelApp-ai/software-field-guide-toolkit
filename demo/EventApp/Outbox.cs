namespace EventApp;

/// <summary>
/// Compliant outbox (#60 demo): a publish waits in the pending queue, and
/// dispatch is idempotent — a double MarkDispatched never double-sends and
/// never drops a send.
/// </summary>
public sealed class Outbox
{
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dispatched = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Pending => _pending.ToList();

    public IReadOnlyList<string> Dispatched => _dispatched.ToList();

    public void Publish(string messageId) => _pending.Add(messageId);

    public void MarkDispatched(string messageId)
    {
        if (_pending.Remove(messageId))
        {
            _dispatched.Add(messageId);
        }
    }
}
