using System.Text.Json;

namespace EventApp;

/// <summary>
/// Compliant order-events consumer (#60 demo): dedupes by message id
/// (at-least-once delivery, exactly-once effects), tolerates out-of-order
/// arrival, routes poison to the dead letters instead of crashing, and
/// applies old-schema replays rather than losing them.
/// </summary>
public sealed class OrderEventsConsumer
{
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly List<string> _appliedEffects = new();
    private readonly List<string> _deadLetters = new();

    public IReadOnlyList<string> AppliedEffects => _appliedEffects;

    public IReadOnlyList<string> DeadLetters => _deadLetters;

    public void Handle(string message)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            DeadLetter(message);
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetString(document.RootElement, "id", out var id) ||
                !TryGetString(document.RootElement, "type", out var type) ||
                !document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !document.RootElement.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                DeadLetter(message);
                return;
            }

            if (type != "order.placed" && type != "invoice.issued")
            {
                DeadLetter(message);
                return;
            }

            if (versionElement.GetInt32() < 1)
            {
                DeadLetter(message);
                return;
            }

            if (_applied.Contains(id))
            {
                return; // duplicate delivery — the effect is already applied
            }

            // Old-schema replays (version < current) are tolerated: the
            // payload is applied as delivered, never lost.
            _applied.Add(id);
            _appliedEffects.Add(id);
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(property, out var child) &&
            child.ValueKind == JsonValueKind.String &&
            (value = child.GetString() ?? string.Empty).Length > 0;
    }

    private void DeadLetter(string message) => _deadLetters.Add(message);
}
