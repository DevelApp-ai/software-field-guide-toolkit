using System.Text.Json;

namespace Sfg.Toolkit.BiasProbe;

/// <summary>
/// One cohort-sliced metric the probe run is judged by: the threshold is the
/// floor for the overall aggregate and for every cohort slice.
/// </summary>
public sealed record BiasMetric(string Id, double Threshold);

/// <summary>
/// The bias-probe configuration (bias-probe.json) — the probe pack's required
/// input. Parsed with loud errors: the corpus shares must sum to 1, metric ids
/// must be unique, thresholds must be numbers in (0, 1], and the delta ceilings
/// must be numbers in [0, 1]. File paths inside the config resolve relative to
/// the config file's directory, so a suite can be run from any working
/// directory.
/// </summary>
public sealed class BiasProbeConfig
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Path the config was loaded from, for findings evidence.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Raw config text, for line-anchored citations.</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>1-based line of the first line containing the needle, for citations.</summary>
    public string Anchor(string needle)
    {
        var line = 1;
        using var reader = new StringReader(Raw);
        while (reader.ReadLine() is { } current)
        {
            if (current.Contains(needle, StringComparison.Ordinal))
            {
                return $"{Path.GetFileName(ConfigPath)}:{line}";
            }

            line++;
        }

        return Path.GetFileName(ConfigPath);
    }

    public string Judge { get; init; } = string.Empty;

    /// <summary>The floor for the counterfactual consistency rate; default 0.9.</summary>
    public double ConsistencyFloor { get; init; } = 0.9;

    /// <summary>The ceiling for the refusal-rate delta across cohorts; default 0.1.</summary>
    public double RefusalDeltaMax { get; init; } = 0.1;

    /// <summary>The band for citation share vs the declared corpus shares; default 0.15.</summary>
    public double CitationDeltaMax { get; init; } = 0.15;

    /// <summary>Source cohort → the share of citations it is expected to supply. Sums to 1.</summary>
    public IReadOnlyDictionary<string, double> CorpusShares { get; init; } = new Dictionary<string, double>();

    public IReadOnlyList<BiasMetric> Metrics { get; init; } = Array.Empty<BiasMetric>();

    public string PairsPath { get; init; } = string.Empty;

    public string CasesPath { get; init; } = string.Empty;

    public string ResultsPath { get; init; } = string.Empty;

    public string BaselinePath { get; init; } = string.Empty;

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public static BiasProbeConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"bias-probe config not found: {path ?? "<null>"} — pass it with --config bias-probe.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"bias-probe config is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("bias-probe config must be a JSON object with a \"metrics\" array");
            }

            var name = ReadString(root, "name") ?? throw new InvalidOperationException(
                "bias-probe config needs a non-empty \"name\" — what this probe pack measures");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("bias-probe config \"name\" must be non-empty");
            }

            var judge = ReadString(root, "judge") ?? throw new InvalidOperationException(
                "bias-probe config needs a non-empty \"judge\" — the judge configuration id, published with every report");
            if (string.IsNullOrWhiteSpace(judge))
            {
                throw new InvalidOperationException("bias-probe config \"judge\" must be non-empty");
            }

            var consistencyFloor = 0.9;
            if (root.TryGetProperty("consistencyFloor", out var floorElement))
            {
                if (floorElement.ValueKind != JsonValueKind.Number || !IsValidFraction(floorElement.GetDouble()))
                {
                    throw new InvalidOperationException("\"consistencyFloor\" must be a number in [0, 1]");
                }

                consistencyFloor = floorElement.GetDouble();
            }

            var refusalDeltaMax = 0.1;
            if (root.TryGetProperty("refusalDeltaMax", out var refusalElement))
            {
                if (refusalElement.ValueKind != JsonValueKind.Number || !IsValidFraction(refusalElement.GetDouble()))
                {
                    throw new InvalidOperationException("\"refusalDeltaMax\" must be a number in [0, 1]");
                }

                refusalDeltaMax = refusalElement.GetDouble();
            }

            var citationDeltaMax = 0.15;
            if (root.TryGetProperty("citationDeltaMax", out var citationElement))
            {
                if (citationElement.ValueKind != JsonValueKind.Number || !IsValidFraction(citationElement.GetDouble()))
                {
                    throw new InvalidOperationException("\"citationDeltaMax\" must be a number in [0, 1]");
                }

                citationDeltaMax = citationElement.GetDouble();
            }

            if (!root.TryGetProperty("corpusShares", out var sharesElement) ||
                sharesElement.ValueKind != JsonValueKind.Object ||
                !sharesElement.EnumerateObject().Any())
            {
                throw new InvalidOperationException(
                    "bias-probe config needs a non-empty \"corpusShares\" object — the share of citations each source cohort is expected to supply");
            }

            var corpusShares = new Dictionary<string, double>(StringComparer.Ordinal);
            double sum = 0;
            foreach (var property in sharesElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    throw new InvalidOperationException("\"corpusShares\" has an empty cohort name");
                }

                if (property.Value.ValueKind != JsonValueKind.Number ||
                    property.Value.GetDouble() is <= 0.0 or > 1.0)
                {
                    throw new InvalidOperationException(
                        $"corpus share for '{property.Name}' must be a number in (0, 1]");
                }

                corpusShares[property.Name] = property.Value.GetDouble();
                sum += property.Value.GetDouble();
            }

            if (Math.Abs(sum - 1.0) > 0.000001)
            {
                throw new InvalidOperationException(
                    $"\"corpusShares\" must sum to 1 — the declared shares sum to {sum:0.####}");
            }

            if (!root.TryGetProperty("metrics", out var metricsElement) ||
                metricsElement.ValueKind != JsonValueKind.Array || metricsElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "bias-probe config needs a non-empty \"metrics\" array — the cohort-sliced metrics the run is judged by");
            }

            var metrics = new List<BiasMetric>();
            foreach (var entry in metricsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every metric must be a JSON object");
                }

                var id = ReadString(entry, "id") ?? throw new InvalidOperationException(
                    "every metric needs a non-empty \"id\"");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("every metric needs a non-empty \"id\"");
                }

                if (!entry.TryGetProperty("threshold", out var thresholdElement) ||
                    thresholdElement.ValueKind != JsonValueKind.Number ||
                    thresholdElement.GetDouble() is <= 0.0 or > 1.0)
                {
                    throw new InvalidOperationException(
                        $"metric '{id}' needs a \"threshold\" that is a number in (0, 1]");
                }

                metrics.Add(new BiasMetric(id, thresholdElement.GetDouble()));
            }

            foreach (var group in metrics.GroupBy(m => m.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException($"duplicate metric id '{group.Key}' — ambiguous");
                }
            }

            var pairsPath = ReadString(root, "pairs") ?? throw new InvalidOperationException(
                "bias-probe config needs a \"pairs\" file — the counterfactual pairs, committed with the suite");
            var casesPath = ReadString(root, "cases") ?? throw new InvalidOperationException(
                "bias-probe config needs a \"cases\" file — the labeled set, committed with the suite");
            var resultsPath = ReadString(root, "results") ?? throw new InvalidOperationException(
                "bias-probe config needs a \"results\" file — the recorded run to judge (override with --results)");
            var baselinePath = ReadString(root, "baseline") ?? throw new InvalidOperationException(
                "bias-probe config needs a \"baseline\" file — the committed baseline the gate compares against");

            var maxFindings = 50;
            if (root.TryGetProperty("maxFindings", out var maxElement))
            {
                if (maxElement.ValueKind != JsonValueKind.Number ||
                    !maxElement.TryGetInt32(out maxFindings) || maxFindings < 1)
                {
                    throw new InvalidOperationException("\"maxFindings\" must be a positive integer");
                }
            }

            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return new BiasProbeConfig
            {
                Name = name,
                ConfigPath = path,
                Raw = text,
                Judge = judge,
                ConsistencyFloor = consistencyFloor,
                RefusalDeltaMax = refusalDeltaMax,
                CitationDeltaMax = citationDeltaMax,
                CorpusShares = corpusShares,
                Metrics = metrics.ToArray(),
                PairsPath = Resolve(configDirectory, pairsPath),
                CasesPath = Resolve(configDirectory, casesPath),
                ResultsPath = Resolve(configDirectory, resultsPath),
                BaselinePath = Resolve(configDirectory, baselinePath),
                MaxFindings = maxFindings,
            };
        }
    }

    public double ThresholdOf(string metric) =>
        Metrics.FirstOrDefault(m => string.Equals(m.Id, metric, StringComparison.Ordinal))?.Threshold ?? 0;

    internal static string Resolve(string configDirectory, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(configDirectory, path);

    internal static bool IsValidFraction(double value) => value is >= 0.0 and <= 1.0;

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }
}

