using System.Text.Json;

namespace Sfg.Toolkit.EvalHarness;

/// <summary>One case of the labeled set (cases.json, committed with the suite).</summary>
public sealed record EvalCase
{
    public required string Id { get; init; }

    public required string Stage { get; init; }

    public required string Input { get; init; }

    /// <summary>The case's expected outcome (the label the suite was built with).</summary>
    public required string Expected { get; init; }

    /// <summary>The human call on this case ("correct"/"incorrect"); cases that carry
    /// it form the human-labeled sample the judge is calibrated against.</summary>
    public string? HumanLabel { get; init; }

    public bool Labeled => HumanLabel is not null;
}

/// <summary>One recorded outcome of a run (results.json), produced by the system
/// under evaluation — the harness judges it; it never generates it.</summary>
public sealed record EvalResult
{
    public required string Id { get; init; }

    public required string Predicted { get; init; }

    /// <summary>The judge's verdict: "correct", "incorrect", or "abstain".</summary>
    public required string JudgeVerdict { get; init; }

    /// <summary>Per-metric scores in [0, 1] for this case.</summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();
}

/// <summary>A finding, always with file:line evidence — a regression cites the
/// cases that moved, never just the aggregate.</summary>
public sealed record EvalFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
{
    public static readonly IComparer<EvalFinding> Comparer = Comparer<EvalFinding>.Create((left, right) =>
    {
        var severity = string.CompareOrdinal(left.Severity, right.Severity);
        return severity != 0 ? severity : string.CompareOrdinal(left.Rule, right.Rule) is var rule && rule != 0
            ? rule
            : string.CompareOrdinal(left.Message, right.Message);
    });
}

/// <summary>The committed baseline (baseline.json): per-metric aggregate values
/// plus per-case scores, so a regression can cite exactly the cases that moved.</summary>
public sealed record EvalBaseline
{
    public required IReadOnlyDictionary<string, double> MetricValues { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> CaseScores { get; init; }
}

/// <summary>The judged run: per-case results, per-stage aggregates, judge
/// agreement over the human-labeled sample, and the gate's findings.</summary>
public sealed class EvalReport
{
    public required string RunName { get; init; }

    public required IReadOnlyList<EvalCase> Cases { get; init; }

    public required IReadOnlyList<EvalResult> Results { get; init; }

    /// <summary>Metric → aggregate over the metric's stage cases (cases with no
    /// score for the metric are excluded and flagged, never guessed).</summary>
    public required IReadOnlyDictionary<string, double> Aggregates { get; init; }

    /// <summary>Metric → the case scores that produced the aggregate, ordinal-sorted.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<(string CaseId, double Score)>> PerCase { get; init; }

    /// <summary>Metric → (threshold or null, baseline value or null) — what the gate judged against.</summary>
    public required IReadOnlyDictionary<string, (double? Threshold, double? Baseline)> GateTargets { get; init; }

    public int LabeledSampleSize { get; init; }

    public int JudgeAgrees { get; init; }

    public int JudgeDisagrees { get; init; }

    public int JudgeAbstains { get; init; }

    /// <summary>Case ids (ordinal-sorted) where the judge disagreed with the human label.</summary>
    public required IReadOnlyList<string> JudgeDisagreementCases { get; init; }

    public required IReadOnlyList<EvalFinding> Findings { get; init; }

    public IReadOnlyList<EvalFinding> RenderedFindings => Findings.Take(Cap).ToArray();

    public int Cap { get; init; }

    public int Errors => Findings.Count(f => f.Severity == "error");

    public int Warnings => Findings.Count(f => f.Severity == "warning");

    public double Agreement =>
        LabeledSampleSize == 0 ? 0.0 : Math.Round((double)JudgeAgrees / LabeledSampleSize, 4, MidpointRounding.AwayFromZero);
}

/// <summary>Loads the suite's data files with loud errors and line-anchored
/// citations: the raw text is kept so every finding can cite file:line.</summary>
public sealed class EvalFile<T>
{
    public required string Path { get; init; }

    public required string Raw { get; init; }

    public required T Value { get; init; }

    /// <summary>1-based line of the first line containing <paramref name="needle"/>, or 0.</summary>
    public int LineOf(string needle)
    {
        var line = 1;
        using var reader = new StringReader(Raw);
        while (reader.ReadLine() is { } current)
        {
            if (current.Contains(needle, StringComparison.Ordinal))
            {
                return line;
            }

            line++;
        }

        return 0;
    }

    public string Anchor(string needle) => LineOf(needle) is var line && line > 0
        ? $"{System.IO.Path.GetFileName(Path)}:{line}"
        : System.IO.Path.GetFileName(Path);
}

public static class EvalFiles
{
    public static EvalFile<T> Load<T>(string path, Func<JsonElement, T> parse)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"eval data file not found: {path}");
        }

        var raw = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"eval data file is not valid JSON: {path}: {exception.Message}");
        }

        using (document)
        {
            return new EvalFile<T> { Path = path, Raw = raw, Value = parse(document.RootElement) };
        }
    }

    public static IReadOnlyList<EvalCase> ParseCases(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cases", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("cases file needs a \"cases\" array");
        }

        var cases = new List<EvalCase>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("every case must be a JSON object");
            }

            var id = RequireString(entry, "id", "case");
            var stage = RequireString(entry, "stage", "case");
            var input = RequireString(entry, "input", "case");
            var expected = RequireString(entry, "expected", "case");
            string? human = null;
            if (entry.TryGetProperty("human", out var humanElement))
            {
                human = humanElement.ValueKind == JsonValueKind.String ? humanElement.GetString() : null;
                if (human is not ("correct" or "incorrect"))
                {
                    throw new InvalidOperationException(
                        $"case '{id}' has an invalid \"human\" label '{human}' — \"correct\" or \"incorrect\" only");
                }
            }

            cases.Add(new EvalCase { Id = id, Stage = stage, Input = input, Expected = expected, HumanLabel = human });
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException("the labeled set is empty — there is nothing to evaluate");
        }

        return cases;
    }

    public static (string RunName, IReadOnlyList<EvalResult> Results) ParseResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("run", out var runElement) ||
            runElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("results", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("results file needs a \"run\" name and a \"results\" array");
        }

        var results = new List<EvalResult>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("every result must be a JSON object");
            }

            var id = RequireString(entry, "id", "result");
            var predicted = RequireString(entry, "predicted", "result");
            var verdict = RequireString(entry, "judge", "result");
            if (verdict is not ("correct" or "incorrect" or "abstain"))
            {
                throw new InvalidOperationException(
                    $"result '{id}' has an invalid judge verdict '{verdict}' — \"correct\", \"incorrect\", or \"abstain\" only");
            }

            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            if (entry.TryGetProperty("scores", out var scoresElement))
            {
                if (scoresElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"result '{id}' needs \"scores\" as an object of metric → number");
                }

                foreach (var score in scoresElement.EnumerateObject())
                {
                    if (score.Value.ValueKind != JsonValueKind.Number ||
                        !EvalHarnessConfig.IsValidScore(score.Value.GetDouble()))
                    {
                        throw new InvalidOperationException(
                            $"result '{id}' has a score for '{score.Name}' that is not a number in [0, 1]");
                    }

                    scores[score.Name] = score.Value.GetDouble();
                }
            }

            results.Add(new EvalResult { Id = id, Predicted = predicted, JudgeVerdict = verdict, Scores = scores });
        }

        return (runElement.GetString()!, results);
    }

    public static EvalBaseline ParseBaseline(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("metrics", out var metricsElement) ||
            metricsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "baseline file needs a \"metrics\" object of metric → { value, cases }");
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var cases = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var metric in metricsElement.EnumerateObject())
        {
            if (metric.Value.ValueKind != JsonValueKind.Object ||
                !metric.Value.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException($"baseline metric '{metric.Name}' needs a numeric \"value\"");
            }

            values[metric.Name] = valueElement.GetDouble();

            var caseScores = new Dictionary<string, double>(StringComparer.Ordinal);
            if (metric.Value.TryGetProperty("cases", out var casesElement))
            {
                if (casesElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"baseline metric '{metric.Name}' needs \"cases\" as an object of case id → score");
                }

                foreach (var pair in casesElement.EnumerateObject())
                {
                    if (pair.Value.ValueKind != JsonValueKind.Number)
                    {
                        throw new InvalidOperationException(
                            $"baseline metric '{metric.Name}' has a non-numeric score for case '{pair.Name}'");
                    }

                    caseScores[pair.Name] = pair.Value.GetDouble();
                }
            }

            cases[metric.Name] = caseScores;
        }

        return new EvalBaseline { MetricValues = values, CaseScores = cases };
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
}
