using System.Text.Json;

namespace EventApp;

/// <summary>
/// Naive consumer (the failure seed): no deduplication, no poison handling —
/// malformed payloads crash the handler — and old-schema replays are rejected
/// outright. The conformance suite must fail this consumer loudly.
/// </summary>
public sealed class OrderEventsConsumer
{
    public IReadOnlyList<string> AppliedEffects { get; } = new List<string>();

    public IReadOnlyList<string> DeadLetters { get; } = new List<string>();

    public void Handle(string message)
    {
        using var document = JsonDocument.Parse(message); // poison crashes here
        var id = document.RootElement.GetProperty("id").GetString()!;
        var version = document.RootElement.GetProperty("version").GetInt32();
        if (version < 2)
        {
            throw new InvalidOperationException($"unsupported message version {version} — refusing old schemas");
        }

        ((List<string>)AppliedEffects).Add(id); // no dedup — duplicates apply twice
    }
}
