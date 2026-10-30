using System.Globalization;
using System.Text.Json;

namespace Sfg.Toolkit.DrillHarness;

/// <summary>
/// One drill scenario from the committed catalog: the failure it injects, the
/// telemetry signals the drill must record, and the clocks it is judged
/// against — the alert clock (injection → alert), the restore clock (injection
/// → restored, the RTO), and the data-loss window (last recoverable data →
/// restored, the RPO).
/// </summary>
public sealed record DrillScenario
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Failure { get; init; } = string.Empty;

    public IReadOnlyList<string> ExpectedSignals { get; init; } = Array.Empty<string>();

    /// <summary>Injection → alert budget, in seconds.</summary>
    public required double AlertSeconds { get; init; }

    /// <summary>Injection → restore budget (RTO), in seconds.</summary>
    public required double RtoSeconds { get; init; }

    /// <summary>Last recoverable data → restore budget (RPO), in seconds.</summary>
    public required double RpoSeconds { get; init; }

    /// <summary>Scenario-level staleness override; null falls back to the config window.</summary>
    public int? MaxStalenessDays { get; init; }

    /// <summary>1-based line of this scenario's row in scenarios.json, for citations.</summary>
    public int Line { get; init; }

    public string Anchor(string fileName) => $"{fileName}:{Line}";
}

/// <summary>One recorded telemetry signal inside a drill record.</summary>
public sealed record DrillSignal(string Key, DateTimeOffset At);

/// <summary>
/// One recorded drill: the scenario rehearsed, the clock readings, the
/// recorded signals, and who/what took part. The harness judges this export
/// post-hoc; it never runs the drill.
/// </summary>
public sealed record DrillRecord
{
    public required string Scenario { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the alert fired; null = the alert never fired.</summary>
    public DateTimeOffset? AlertedAt { get; init; }

    /// <summary>When service was restored; null = never restored within the record.</summary>
    public DateTimeOffset? RestoredAt { get; init; }

    /// <summary>Timestamp of the last recoverable data before the restore.</summary>
    public DateTimeOffset? LastRecoverableAt { get; init; }

    public IReadOnlyList<DrillSignal> Signals { get; init; } = Array.Empty<DrillSignal>();

    public IReadOnlyList<string> Participants { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Versions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>1-based line of this record's row in the evidence file, for citations.</summary>
    public int Line { get; init; }

    public string Anchor(string fileName) => $"{fileName}:{Line}";
}

/// <summary>
/// The recorded drill export: one drill run id, the export's reference time
/// (staleness is measured against this recorded instant — never the wall
/// clock, so the analysis is deterministic), and the drill records.
/// </summary>
public sealed record DrillEvidence
{
    public string Drill { get; init; } = string.Empty;

    public required DateTimeOffset AsOf { get; init; }

    public IReadOnlyList<DrillRecord> Records { get; init; } = Array.Empty<DrillRecord>();
}

/// <summary>Severity-ordered finding: errors before warnings, then rule, then message.</summary>
public sealed record DrillFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
    : IComparable<DrillFinding>
{
    public int CompareTo(DrillFinding? other)
    {
        if (other is null)
        {
            return 1;
        }

        var severityRank = Severity == "error" ? 0 : 1;
        var otherRank = other.Severity == "error" ? 0 : 1;
        var severity = severityRank.CompareTo(otherRank);
        if (severity != 0)
        {
            return severity;
        }

        var rule = string.CompareOrdinal(Rule, other.Rule);
        if (rule != 0)
        {
            return rule;
        }

        return string.CompareOrdinal(Message, other.Message);
    }
}

/// <summary>Per-scenario drill freshness: when it was last rehearsed, and against which window.</summary>
public sealed record FreshnessRow(
    string Scenario,
    string Title,
    DateTimeOffset? LastDrilled,
    double? AgeDays,
    int StalenessDays,
    string Status,
    string LastAnchor);

/// <summary>Per-record clock readings against the scenario's declared clocks.</summary>
public sealed record ClockRow(
    string Scenario,
    double? AlertSeconds,
    double AlertClock,
    double? RestoreSeconds,
    double RtoClock,
    double? RpoSeconds,
    double RpoClock,
    string Verdict,
    string Anchor);

/// <summary>Per-record expected-signal coverage.</summary>
public sealed record SignalRow(string Scenario, IReadOnlyList<string> Missing, string Anchor);

/// <summary>Per-record participants and versions coverage.</summary>
public sealed record ParticipationRow(
    string Scenario,
    IReadOnlyList<string> Participants,
    IReadOnlyList<string> Versions,
    string Verdict,
    string Anchor);

/// <summary>The analyzed drill report, deterministic for a given export.</summary>
public sealed record DrillReport
{
    public required string Suite { get; init; }

    public required string Judge { get; init; }

    public required string Drill { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    public int StalenessDays { get; init; }

    public required IReadOnlyList<FreshnessRow> Freshness { get; init; }

    public required IReadOnlyList<ClockRow> Clocks { get; init; }

    public required IReadOnlyList<SignalRow> Signals { get; init; }

    public required IReadOnlyList<ParticipationRow> Participation { get; init; }

    /// <summary>Every finding, severity-ordered (before the cap).</summary>
    public required IReadOnlyList<DrillFinding> Findings { get; init; }

    /// <summary>The findings actually rendered, capped at the config limit.</summary>
    public required IReadOnlyList<DrillFinding> RenderedFindings { get; init; }

    public int Errors { get; init; }

    public int Warnings { get; init; }

    public int Cap { get; init; }
}

/// <summary>
/// Loads the committed drill catalog (scenarios.json) and the recorded drill
/// export (drill-evidence.json) with loud errors — malformed exports never
/// pass silently. Every parsed row carries its 1-based source line so every
/// finding cites file:line evidence.
/// </summary>
public static class DrillFiles
{
    public static IReadOnlyList<DrillScenario> LoadScenarios(string path)
    {
        var text = ReadText(path, "drill scenarios");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"drill scenarios file is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("scenarios", out var scenariosElement) ||
                scenariosElement.ValueKind != JsonValueKind.Array || scenariosElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "drill scenarios file needs a non-empty \"scenarios\" array — the failure-injection catalog");
            }

            var scenarios = new List<DrillScenario>();
            var cursor = 0;
            foreach (var entry in scenariosElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every drill scenario must be a JSON object");
                }

                var id = RequireString(entry, "id", "scenario");
                var line = LineOf(text, $"\"id\": \"{id}", ref cursor);

                var title = ReadString(entry, "title") ?? string.Empty;
                var failure = ReadString(entry, "failure") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(failure))
                {
                    throw new InvalidOperationException(
                        $"scenario '{id}' needs a non-empty \"failure\" — the failure it injects");
                }

                if (!entry.TryGetProperty("expectedSignals", out var signalsElement) ||
                    signalsElement.ValueKind != JsonValueKind.Array ||
                    signalsElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException(
                        $"scenario '{id}' needs a non-empty \"expectedSignals\" array — the telemetry signals a drill must record");
                }

                var signals = new List<string>();
                foreach (var signal in signalsElement.EnumerateArray())
                {
                    if (signal.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(signal.GetString()))
                    {
                        throw new InvalidOperationException(
                            $"scenario '{id}' has an empty expected signal — every signal needs a non-empty key");
                    }

                    signals.Add(signal.GetString()!);
                }

                if (!entry.TryGetProperty("clocks", out var clocksElement) || clocksElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"scenario '{id}' needs a \"clocks\" object — alertSeconds, rtoSeconds, rpoSeconds");
                }

                var alertSeconds = RequirePositiveSeconds(clocksElement, "alertSeconds", id);
                var rtoSeconds = RequirePositiveSeconds(clocksElement, "rtoSeconds", id);
                var rpoSeconds = RequirePositiveSeconds(clocksElement, "rpoSeconds", id);

                int? maxStalenessDays = null;
                if (entry.TryGetProperty("maxStalenessDays", out var stalenessElement))
                {
                    if (stalenessElement.ValueKind != JsonValueKind.Number ||
                        !stalenessElement.TryGetInt32(out var days) || days < 1)
                    {
                        throw new InvalidOperationException(
                            $"scenario '{id}' \"maxStalenessDays\" must be a positive integer");
                    }

                    maxStalenessDays = days;
                }

                scenarios.Add(new DrillScenario
                {
                    Id = id,
                    Title = title,
                    Failure = failure,
                    ExpectedSignals = signals.ToArray(),
                    AlertSeconds = alertSeconds,
                    RtoSeconds = rtoSeconds,
                    RpoSeconds = rpoSeconds,
                    MaxStalenessDays = maxStalenessDays,
                    Line = line,
                });
            }

            foreach (var group in scenarios.GroupBy(s => s.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate drill scenario '{group.Key}' — the catalog must be unambiguous");
                }
            }

            return scenarios.ToArray();
        }
    }

    public static DrillEvidence LoadEvidence(string path)
    {
        var text = ReadText(path, "drill evidence");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"drill evidence file is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("drill evidence must be a JSON object with a \"records\" array");
            }

            var drill = ReadString(root, "drill") ?? throw new InvalidOperationException(
                "drill evidence needs a non-empty \"drill\" — the drill run id this export records");
            if (string.IsNullOrWhiteSpace(drill))
            {
                throw new InvalidOperationException("drill evidence \"drill\" must be non-empty");
            }

            var asOfText = ReadString(root, "asOf") ?? throw new InvalidOperationException(
                "drill evidence needs an \"asOf\" timestamp — the recorded reference instant staleness is measured against");
            if (!TryReadTimestamp(asOfText, out var asOf))
            {
                throw new InvalidOperationException(
                    $"drill evidence \"asOf\" is not a valid ISO-8601 timestamp: {asOfText}");
            }

            if (!root.TryGetProperty("records", out var recordsElement) ||
                recordsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("drill evidence needs a \"records\" array");
            }

            var records = new List<DrillRecord>();
            var cursor = 0;
            foreach (var entry in recordsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every drill record must be a JSON object");
                }

                var scenario = RequireString(entry, "scenario", "drill record");
                var line = LineOf(text, $"\"scenario\": \"{scenario}", ref cursor);

                var startedText = RequireString(entry, "startedAt", "drill record");
                if (!TryReadTimestamp(startedText, out var startedAt))
                {
                    throw new InvalidOperationException(
                        $"drill record for '{scenario}' has a malformed \"startedAt\": {startedText}");
                }

                DateTimeOffset? alertedAt = ReadOptionalTimestamp(entry, "alertedAt", scenario);
                DateTimeOffset? restoredAt = ReadOptionalTimestamp(entry, "restoredAt", scenario);
                DateTimeOffset? lastRecoverableAt = ReadOptionalTimestamp(entry, "lastRecoverableAt", scenario);

                var signals = new List<DrillSignal>();
                if (entry.TryGetProperty("signals", out var signalsElement))
                {
                    if (signalsElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"drill record for '{scenario}' \"signals\" must be an array");
                    }

                    foreach (var signal in signalsElement.EnumerateArray())
                    {
                        if (signal.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidOperationException(
                                $"drill record for '{scenario}' has a signal that is not a JSON object");
                        }

                        var key = RequireString(signal, "key", $"signal in record '{scenario}'");
                        var atText = RequireString(signal, "at", $"signal '{key}' in record '{scenario}'");
                        if (!TryReadTimestamp(atText, out var at))
                        {
                            throw new InvalidOperationException(
                                $"signal '{key}' in record '{scenario}' has a malformed \"at\": {atText}");
                        }

                        signals.Add(new DrillSignal(key, at));
                    }
                }

                var participants = new List<string>();
                if (entry.TryGetProperty("participants", out var participantsElement))
                {
                    if (participantsElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"drill record for '{scenario}' \"participants\" must be an array");
                    }

                    foreach (var participant in participantsElement.EnumerateArray())
                    {
                        if (participant.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(participant.GetString()))
                        {
                            throw new InvalidOperationException(
                                $"drill record for '{scenario}' has an empty participant — every participant needs a name");
                        }

                        participants.Add(participant.GetString()!);
                    }
                }

                var versions = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.TryGetProperty("versions", out var versionsElement))
                {
                    if (versionsElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"drill record for '{scenario}' \"versions\" must be an object");
                    }

                    foreach (var property in versionsElement.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(property.Name) ||
                            property.Value.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(property.Value.GetString()))
                        {
                            throw new InvalidOperationException(
                                $"drill record for '{scenario}' has an empty version entry — every component needs a version");
                        }

                        versions[property.Name] = property.Value.GetString()!;
                    }
                }

                records.Add(new DrillRecord
                {
                    Scenario = scenario,
                    StartedAt = startedAt,
                    AlertedAt = alertedAt,
                    RestoredAt = restoredAt,
                    LastRecoverableAt = lastRecoverableAt,
                    Signals = signals.ToArray(),
                    Participants = participants.ToArray(),
                    Versions = versions,
                    Line = line,
                });
            }

            return new DrillEvidence
            {
                Drill = drill,
                AsOf = asOf,
                Records = records.ToArray(),
            };
        }
    }

    private static string ReadText(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{what} file not found: {path}");
        }

        return File.ReadAllText(path);
    }

    /// <summary>1-based line of the next occurrence of the needle at or after the cursor; the cursor advances.</summary>
    private static int LineOf(string text, string needle, ref int cursor)
    {
        var index = text.IndexOf(needle, cursor, StringComparison.Ordinal);
        if (index < 0)
        {
            index = Math.Max(0, cursor);
        }

        cursor = index + needle.Length;
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool TryReadTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            timestamp = parsed;
            return true;
        }

        timestamp = default;
        return false;
    }

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement entry, string name, string scenario)
    {
        var text = ReadString(entry, name);
        if (text is null)
        {
            return null;
        }

        if (!TryReadTimestamp(text, out var timestamp))
        {
            throw new InvalidOperationException(
                $"drill record for '{scenario}' has a malformed \"{name}\": {text}");
        }

        return timestamp;
    }

    private static double RequirePositiveSeconds(JsonElement clocks, string name, string scenarioId)
    {
        if (!clocks.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number || element.GetDouble() <= 0)
        {
            throw new InvalidOperationException(
                $"scenario '{scenarioId}' needs a \"{name}\" clock that is a positive number of seconds");
        }

        return element.GetDouble();
    }

    private static string RequireString(JsonElement entry, string name, string what)
    {
        if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidOperationException($"every {what} needs a non-empty \"{name}\"");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }
}
