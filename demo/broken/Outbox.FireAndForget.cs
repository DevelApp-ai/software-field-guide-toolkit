namespace EventApp;

/// <summary>
/// Fire-and-forget outbox (the failure seed): publishes vanish and dispatches
/// leave no bookkeeping. The outbox health probes must fail this loudly.
/// </summary>
public sealed class Outbox
{
    public IReadOnlyList<string> Pending => Array.Empty<string>();

    public IReadOnlyList<string> Dispatched => Array.Empty<string>();

    public void Publish(string messageId)
    {
        // fire-and-forget: the message is "sent" immediately, nothing waits
    }

    public void MarkDispatched(string messageId)
    {
        // no bookkeeping
    }
}
