using System.Text.Json;

namespace Sfg.Toolkit.DoraMetrics;

/// <summary>
/// The extractor's optional configuration (dora.json). Defaults: the events
/// export lives at <root>/dora-events.json, the series is trended per ISO
/// week. Paths inside the config resolve relative to the root, so the same
/// config works from any working directory.
/// </summary>
public sealed class DoraConfig
{
    /// <summary>The repository root the paths resolve against.</summary>
    public required string Root { get; init; }

    /// <summary>The recorded events export (dora-events.json) — the only
    /// input the extractor ever reads. It never calls an API live: wiring a
    /// live system means producing an export in this documented format.</summary>
    public string EventsPath { get; init; } = "dora-events.json";

    /// <summary>The series period: day, week (ISO, Monday-anchored), or month.</summary>
    public DoraPeriod Period { get; init; } = DoraPeriod.Week;

    /// <summary>Verbatim events path from --events: overrides config and
    /// default, resolves against the working directory (CI variants live in
    /// demo/broken/ and are passed as-is).</summary>
    public string? EventsOverride { get; init; }

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public string EventsFullPath => EventsOverride ?? Path.Combine(Root, EventsPath);

    public static DoraConfig Load(string? path, string root, string? eventsOverride)
    {
        var config = new DoraConfig { Root = root };
        if (path is not null)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"dora config not found: {path}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"dora config is not valid JSON: {exception.Message}");
            }

            using (document)
            {
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("dora config must be a JSON object");
                }

                string? events = null;
                if (element.TryGetProperty("events", out var eventsElement))
                {
                    if (eventsElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(eventsElement.GetString()))
                    {
                        throw new InvalidOperationException("\"events\" must be a non-empty string — the recorded events export file");
                    }

                    events = eventsElement.GetString();
                }

                var period = DoraPeriod.Week;
                if (element.TryGetProperty("period", out var periodElement))
                {
                    if (periodElement.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException("\"period\" must be a string: day, week, or month");
                    }

                    period = periodElement.GetString() switch
                    {
                        "day" => DoraPeriod.Day,
                        "week" => DoraPeriod.Week,
                        "month" => DoraPeriod.Month,
                        var invalid => throw new InvalidOperationException(
                            $"\"period\" must be day, week, or month — '{invalid}' is not a period"),
                    };
                }

                var maxFindings = 50;
                if (element.TryGetProperty("maxFindings", out var maxElement))
                {
                    if (maxElement.ValueKind != JsonValueKind.Number ||
                        !maxElement.TryGetInt32(out maxFindings) || maxFindings < 1)
                    {
                        throw new InvalidOperationException("\"maxFindings\" must be a positive integer");
                    }
                }

                config = new DoraConfig
                {
                    Root = root,
                    EventsPath = events ?? "dora-events.json",
                    Period = period,
                    MaxFindings = maxFindings,
                };
            }
        }

        // --events overrides both the config and the default, verbatim
        if (eventsOverride is not null)
        {
            config = new DoraConfig
            {
                Root = config.Root,
                EventsPath = config.EventsPath,
                Period = config.Period,
                EventsOverride = eventsOverride,
                MaxFindings = config.MaxFindings,
            };
        }

        return config;
    }
}
