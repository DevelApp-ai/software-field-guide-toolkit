using System.Text.Json;

namespace Sfg.Toolkit.EvalHarness;

/// <summary>
/// One pipeline stage of the eval suite (e.g. retrieval, generation,
/// scoring) with the metrics the stage is judged by.
/// </summary>
public sealed record EvalStage(string Id, IReadOnlyList<string> Metrics);

/// <summary>
/// The eval-suite configuration (eval.json) — the harness's required input.
/// Parsed with loud errors: duplicate stage ids, duplicate metrics (the
/// metrics key thresholds and baselines globally, so they must be unique),
/// thresholds for undeclared metrics, and empty stages never pass silently.
/// File paths inside the config resolve relative to the config file's
/// directory, so a suite can be run from any working directory.
/// </summary>
public sealed class EvalHarnessConfig
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

    public IReadOnlyList<EvalStage> Stages { get; init; } = Array.Empty<EvalStage>();

    public IReadOnlyDictionary<string, double> Thresholds { get; init; } = new Dictionary<string, double>();

    public string CasesPath { get; init; } = string.Empty;

    public string ResultsPath { get; init; } = string.Empty;

    public string BaselinePath { get; init; } = string.Empty;

    /// <summary>Cap on rendered findings (ranked errors-first); default 50.</summary>
    public int MaxFindings { get; init; } = 50;

    public static EvalHarnessConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"eval config not found: {path ?? "<null>"} — pass it with --config eval.json");
        }

        var text = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"eval config is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("eval config must be a JSON object with a \"stages\" array");
            }

            var name = ReadString(root, "name") ?? throw new InvalidOperationException(
                "eval config needs a non-empty \"name\" — what this suite evaluates");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("eval config \"name\" must be non-empty");
            }

            var judge = ReadString(root, "judge") ?? throw new InvalidOperationException(
                "eval config needs a non-empty \"judge\" — the judge configuration id, published with every report");
            if (string.IsNullOrWhiteSpace(judge))
            {
                throw new InvalidOperationException("eval config \"judge\" must be non-empty");
            }

            if (!root.TryGetProperty("stages", out var stagesElement) ||
                stagesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "eval config needs a \"stages\" array — retrieval, generation, scoring, in the order they run");
            }

            var stages = new List<EvalStage>();
            foreach (var entry in stagesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every stage must be a JSON object");
                }

                var id = ReadString(entry, "id") ?? throw new InvalidOperationException(
                    "every stage needs a non-empty \"id\"");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("every stage needs a non-empty \"id\"");
                }

                if (!entry.TryGetProperty("metrics", out var metricsElement) ||
                    metricsElement.ValueKind != JsonValueKind.Array ||
                    metricsElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException(
                        $"stage '{id}' needs a non-empty \"metrics\" array — the metrics its cases are scored by");
                }

                var metrics = new List<string>();
                foreach (var metric in metricsElement.EnumerateArray())
                {
                    if (metric.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(metric.GetString()))
                    {
                        throw new InvalidOperationException(
                            $"stage '{id}' has an empty metric name — every metric must be a non-empty string");
                    }

                    metrics.Add(metric.GetString()!);
                }

                stages.Add(new EvalStage(id, metrics));
            }

            if (stages.Count == 0)
            {
                throw new InvalidOperationException(
                    "eval config declares no stages — there is nothing to evaluate");
            }

            foreach (var group in stages.GroupBy(s => s.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException($"duplicate stage id '{group.Key}' — ambiguous");
                }
            }

            // metrics key thresholds and baselines globally: they must be unique across stages
            var seenMetrics = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stage in stages)
            {
                foreach (var metric in stage.Metrics)
                {
                    if (!seenMetrics.Add(metric))
                    {
                        throw new InvalidOperationException(
                            $"duplicate metric '{metric}' — metric names are global because thresholds and baselines key on them");
                    }
                }
            }

            var thresholds = new Dictionary<string, double>(StringComparer.Ordinal);
            if (root.TryGetProperty("thresholds", out var thresholdsElement))
            {
                if (thresholdsElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("\"thresholds\" must be an object of metric → minimum value");
                }

                foreach (var property in thresholdsElement.EnumerateObject())
                {
                    if (!seenMetrics.Contains(property.Name))
                    {
                        throw new InvalidOperationException(
                            $"threshold names undeclared metric '{property.Name}' — declare it on a stage first");
                    }

                    if (property.Value.ValueKind != JsonValueKind.Number || !IsValidScore(property.Value.GetDouble()))
                    {
                        throw new InvalidOperationException(
                            $"threshold for '{property.Name}' must be a number in [0, 1]");
                    }

                    thresholds[property.Name] = property.Value.GetDouble();
                }
            }

            var casesPath = ReadString(root, "cases") ?? throw new InvalidOperationException(
                "eval config needs a \"cases\" file — the labeled set, committed with the suite");
            var resultsPath = ReadString(root, "results") ?? throw new InvalidOperationException(
                "eval config needs a \"results\" file — the recorded run to judge (override with --results)");
            var baselinePath = ReadString(root, "baseline") ?? throw new InvalidOperationException(
                "eval config needs a \"baseline\" file — the committed baseline the gate compares against");

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
            return new EvalHarnessConfig
            {
                Name = name,
                ConfigPath = path,
                Raw = text,
                Judge = judge,
                Stages = stages.ToArray(),
                Thresholds = thresholds,
                CasesPath = Resolve(configDirectory, casesPath),
                ResultsPath = Resolve(configDirectory, resultsPath),
                BaselinePath = Resolve(configDirectory, baselinePath),
                MaxFindings = maxFindings,
            };
        }
    }

    public string StageOf(string metric)
    {
        foreach (var stage in Stages)
        {
            if (stage.Metrics.Contains(metric, StringComparer.Ordinal))
            {
                return stage.Id;
            }
        }

        return string.Empty;
    }

    internal static string Resolve(string configDirectory, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(configDirectory, path);

    internal static bool IsValidScore(double value) => value is >= 0.0 and <= 1.0;

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }
}
