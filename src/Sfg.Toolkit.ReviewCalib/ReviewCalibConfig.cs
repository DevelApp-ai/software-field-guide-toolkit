using System.Text.Json;

namespace Sfg.Toolkit.ReviewCalib;

/// <summary>
/// The harness's optional configuration (review-calib.json). Defaults: the
/// events export lives at <root>/review-events.json, a queue approves at
/// 99.8% with a sub-second median decision latency, a role catches at least
/// 80% of its seeded defects, and calibration evidence is stale after 30
/// days. Paths inside the config resolve relative to the root, so the same
/// config works from any working directory.
/// </summary>
public sealed class ReviewCalibConfig
{
    /// <summary>The repository root the paths resolve against.</summary>
    public required string Root { get; init; }

    /// <summary>The recorded events export (review-events.json) — the only
    /// input the harness ever reads. It never calls an API live: wiring a
    /// live review system means producing an export in this documented
    /// format.</summary>
    public string EventsPath { get; init; } = "review-events.json";

    /// <summary>Verbatim events path from --events: overrides config and
    /// default, resolves against the working directory (CI variants live in
    /// demo/broken/ and are passed as-is).</summary>
    public string? EventsOverride { get; init; }

    /// <summary>Approval rate at which a queue is rubber-stamping (fraction,
    /// default 0.998) — paired with the median-latency floor.</summary>
    public double RubberStampApprovalRate { get; init; } = 0.998;

    /// <summary>Median decision latency in seconds under which a queue is
    /// approving without examining (default 1.0).</summary>
    public double RubberStampMedianSeconds { get; init; } = 1.0;

    /// <summary>Reviews a queue needs before the rubber-stamp error fires —
    /// small queues stay warnings (default 10).</summary>
    public int RubberStampMinReviews { get; init; } = 10;

    /// <summary>Minimum catch rate on seeded defects per role (fraction,
    /// default 0.8).</summary>
    public double CatchRateMin { get; init; } = 0.8;

    /// <summary>Days after which a queue with no seeded-defect decision is
    /// running on stale calibration evidence (default 30).</summary>
    public int StalenessDays { get; init; } = 30;

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public string EventsFullPath => EventsOverride ?? Path.Combine(Root, EventsPath);

    public static ReviewCalibConfig Load(string? path, string root, string? eventsOverride)
    {
        var config = new ReviewCalibConfig { Root = root };
        if (path is not null)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"review-calib config not found: {path}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"review-calib config is not valid JSON: {exception.Message}");
            }

            using (document)
            {
                var element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("review-calib config must be a JSON object");
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

                var rubberApprovalRate = 0.998;
                if (element.TryGetProperty("rubberStampApprovalRate", out var rubberApprovalElement))
                {
                    rubberApprovalRate = ReadRate(rubberApprovalElement, "rubberStampApprovalRate");
                }

                var rubberMedianSeconds = 1.0;
                if (element.TryGetProperty("rubberStampMedianSeconds", out var rubberMedianElement))
                {
                    if (rubberMedianElement.ValueKind != JsonValueKind.Number || rubberMedianElement.GetDouble() <= 0)
                    {
                        throw new InvalidOperationException("\"rubberStampMedianSeconds\" must be a positive number of seconds");
                    }

                    rubberMedianSeconds = rubberMedianElement.GetDouble();
                }

                var rubberMinReviews = 10;
                if (element.TryGetProperty("rubberStampMinReviews", out var rubberMinElement))
                {
                    if (rubberMinElement.ValueKind != JsonValueKind.Number ||
                        !rubberMinElement.TryGetInt32(out rubberMinReviews) || rubberMinReviews < 2)
                    {
                        throw new InvalidOperationException("\"rubberStampMinReviews\" must be an integer of at least 2 — a median needs a sample");
                    }
                }

                var catchRateMin = 0.8;
                if (element.TryGetProperty("catchRateMin", out var catchRateElement))
                {
                    catchRateMin = ReadRate(catchRateElement, "catchRateMin");
                }

                var stalenessDays = 30;
                if (element.TryGetProperty("stalenessDays", out var stalenessElement))
                {
                    if (stalenessElement.ValueKind != JsonValueKind.Number ||
                        !stalenessElement.TryGetInt32(out stalenessDays) || stalenessDays < 1)
                    {
                        throw new InvalidOperationException("\"stalenessDays\" must be a positive integer");
                    }
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

                config = new ReviewCalibConfig
                {
                    Root = root,
                    EventsPath = events ?? "review-events.json",
                    RubberStampApprovalRate = rubberApprovalRate,
                    RubberStampMedianSeconds = rubberMedianSeconds,
                    RubberStampMinReviews = rubberMinReviews,
                    CatchRateMin = catchRateMin,
                    StalenessDays = stalenessDays,
                    MaxFindings = maxFindings,
                };
            }
        }

        // --events overrides both the config and the default, verbatim
        if (eventsOverride is not null)
        {
            config = new ReviewCalibConfig
            {
                Root = config.Root,
                EventsPath = config.EventsPath,
                EventsOverride = eventsOverride,
                RubberStampApprovalRate = config.RubberStampApprovalRate,
                RubberStampMedianSeconds = config.RubberStampMedianSeconds,
                RubberStampMinReviews = config.RubberStampMinReviews,
                CatchRateMin = config.CatchRateMin,
                StalenessDays = config.StalenessDays,
                MaxFindings = config.MaxFindings,
            };
        }

        return config;
    }

    private static double ReadRate(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number || element.GetDouble() is not (> 0 and <= 1))
        {
            throw new InvalidOperationException($"\"{name}\" must be a fraction above 0 and at most 1");
        }

        return element.GetDouble();
    }
}