using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sfg.Toolkit.EventHarness;

/// <summary>
/// One declared event contract: id, current version, the case payloads the
/// conformance probes replay, and the optional old-schema replay (a previous
/// version and its cases — the version-skew scenario replays them against
/// the current consumer).
/// <see cref="Line"/> cites the event's "id" property in the contract file.
/// </summary>
public sealed record EventContract(
    string Id,
    int Version,
    string Cases,
    int? PreviousVersion,
    string? SkewCases,
    int Line);

/// <summary>
/// The event contract file (events.json) — the harness's required input.
/// Parsed with loud errors: duplicate event ids, missing case files,
/// skew without a previous version, and previous versions that are not
/// older never pass silently. Never a guess.
/// </summary>
public sealed class EventHarnessConfig
{
    public string? Project { get; init; }

    public string Consumer { get; init; } = string.Empty;

    public string? Outbox { get; init; }

    public IReadOnlyList<EventContract> Events { get; init; } = Array.Empty<EventContract>();

    public static EventHarnessConfig Default { get; } = new();

    public static EventHarnessConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"event contract not found: {path ?? "<null>"} — pass the contract file with --config events.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"event contract is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("event contract must be a JSON object with an \"events\" array");
            }

            if (!root.TryGetProperty("events", out var eventsElement) ||
                eventsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("event contract must contain an \"events\" array");
            }

            var consumer = ReadString(root, "consumer");
            if (string.IsNullOrWhiteSpace(consumer))
            {
                throw new InvalidOperationException(
                    "event contract needs a \"consumer\" — the class the conformance probes run against");
            }

            var contracts = new List<EventContract>();
            foreach (var entry in eventsElement.EnumerateArray())
            {
                contracts.Add(ReadContract(entry, text));
            }

            if (contracts.Count == 0)
            {
                throw new InvalidOperationException(
                    "event contract declares no events — the conformance suite has nothing to replay");
            }

            foreach (var group in contracts.GroupBy(c => c.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"event contract declares '{group.Key}' {group.Count()} times — an event id must be unique");
                }
            }

            return new EventHarnessConfig
            {
                Project = ReadString(root, "project"),
                Consumer = consumer,
                Outbox = ReadString(root, "outbox"),
                Events = contracts.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(),
            };
        }
    }

    private static EventContract ReadContract(JsonElement entry, string tableText)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("every event entry must be a JSON object");
        }

        var id = ReadString(entry, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("every event entry needs a non-empty \"id\"");
        }

        var version = (int?)ReadNumber(entry, "version") ?? 1;
        if (version < 1)
        {
            throw new InvalidOperationException($"event '{id}': version must be at least 1");
        }

        var cases = ReadString(entry, "cases");
        if (string.IsNullOrWhiteSpace(cases))
        {
            throw new InvalidOperationException(
                $"event '{id}': every event needs \"cases\" — the case payloads the conformance probes replay");
        }

        var previousVersion = (int?)ReadNumber(entry, "previousVersion");
        var skewCases = ReadString(entry, "skewCases");
        if (previousVersion is not null)
        {
            if (previousVersion < 1)
            {
                throw new InvalidOperationException($"event '{id}': previousVersion must be at least 1");
            }

            if (previousVersion >= version)
            {
                throw new InvalidOperationException(
                    $"event '{id}': previousVersion {previousVersion} is not older than the current version {version} — " +
                    "the version-skew scenario replays an OLD schema");
            }

            if (string.IsNullOrWhiteSpace(skewCases))
            {
                throw new InvalidOperationException(
                    $"event '{id}': declares previousVersion {previousVersion} but no \"skewCases\" — " +
                    "the version-skew scenario needs the old-schema payloads to replay");
            }
        }

        return new EventContract(
            id,
            version,
            cases,
            previousVersion,
            skewCases,
            LineOfId(tableText, id));
    }

    private static int LineOfId(string tableText, string id)
    {
        var line = 1;
        foreach (var raw in tableText.Split('\n'))
        {
            if (Regex.IsMatch(raw, "\"id\"\\s*:\\s*\"" + Regex.Escape(id) + "\""))
            {
                return line;
            }

            line++;
        }

        return 1;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        return null;
    }

    private static decimal? ReadNumber(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
        {
            return number;
        }

        return null;
    }
}
