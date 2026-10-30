using System.Text.Json;

namespace Sfg.Toolkit.BiasProbe;

/// <summary>
/// One counterfactual pair: the same input with only the marker swapped. The
/// tool materializes two case ids deterministically — <see cref="CaseA"/> with
/// <see cref="MarkerA"/> and <see cref="CaseB"/> with <see cref="MarkerB"/> —
/// and a pair is consistent only when both legs record the same score for
/// <see cref="Dimension"/>.
/// </summary>
public sealed record BiasPair
{
    public required string Id { get; init; }

    /// <summary>The scored dimension the pair probes (e.g. tone, hedging).</summary>
    public required string Dimension { get; init; }

    public required string MarkerA { get; init; }

    public required string MarkerB { get; init; }

    /// <summary>The input template — must carry the "{marker}" and "{cohort}" slots.</summary>
    public required string Input { get; init; }

    public string CaseA => $"{Id}:a";

    public string CaseB => $"{Id}:b";
}

/// <summary>One case of the labeled set (cases.json, committed with the suite).
/// Extends the #66 eval case format with the bias probe's own fields: the
/// cohort the case belongs to, whether it should-succeed or must-refuse, and
/// the cohort-sliced metrics it declares itself part of.</summary>
public sealed record ProbeCase
{
    public required string Id { get; init; }

    public required string Cohort { get; init; }

    public required string Input { get; init; }

    /// <summary>The case's expected outcome (the label the suite was built with).</summary>
    public required string Expected { get; init; }

    /// <summary>true = must answer, false = must refuse, null = not a refusal case.</summary>
    public bool? ShouldSucceed { get; init; }

    /// <summary>The cohort-sliced metrics this case rides — the population is
    /// declared by cases, never guessed.</summary>
    public IReadOnlyList<string> Metrics { get; init; } = Array.Empty<string>();

    /// <summary>The human call on this case ("correct"/"incorrect"); cases that carry
    /// it form the human-labeled sample the judge is calibrated against.</summary>
    public string? HumanLabel { get; init; }

    public bool Labeled => HumanLabel is not null;
}

/// <summary>One recorded citation of a run result: which source it cites and
/// which source cohort that source belongs to.</summary>
public sealed record ProbeCitation(string Source, string Cohort);

/// <summary>One recorded outcome of a run (results.json), produced by the
/// system under evaluation — the probe judges it; it never generates it.
/// Extends the #66 eval result with a recorded refusal flag and citations.</summary>
public sealed record ProbeResult
{
    public required string Id { get; init; }

    public required string Predicted { get; init; }

    /// <summary>The judge's verdict: "correct", "incorrect", or "abstain".</summary>
    public required string JudgeVerdict { get; init; }

    /// <summary>Whether the system refused to answer this case — recorded, never guessed.</summary>
    public bool Refused { get; init; }

    /// <summary>Per-metric or per-dimension scores in [0, 1] for this case.</summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();

    public IReadOnlyList<ProbeCitation> Citations { get; init; } = Array.Empty<ProbeCitation>();
}

/// <summary>A finding, always with file:line evidence — a regression cites the
/// pairs or cases that moved, never just the aggregate.</summary>
public sealed record BiasFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
{
    public static readonly IComparer<BiasFinding> Comparer = Comparer<BiasFinding>.Create((left, right) =>
    {
        var severity = string.CompareOrdinal(left.Severity, right.Severity);
        return severity != 0 ? severity : string.CompareOrdinal(left.Rule, right.Rule) is var rule && rule != 0
            ? rule
            : string.CompareOrdinal(left.Message, right.Message);
    });
}

/// <summary>One cohort slice of a baseline metric: the cohort's aggregate value
/// plus its per-case scores, so a regression can cite exactly the cases that
/// moved.</summary>
public sealed record BaselineCohort
{
    public required double Value { get; init; }

    public required IReadOnlyDictionary<string, double> CaseScores { get; init; }
}

/// <summary>One metric's committed baseline: the overall aggregate value with
/// per-case scores, plus the cohort slices with their own per-case scores.</summary>
public sealed record BaselineMetric
{
    public required double Value { get; init; }

    public required IReadOnlyDictionary<string, double> CaseScores { get; init; }

    public required IReadOnlyDictionary<string, BaselineCohort> Cohorts { get; init; }
}

/// <summary>The committed baseline (baseline.json): the counterfactual
/// consistency rate with its per-pair outcomes, plus per-metric aggregates
/// and cohort slices — all with per-case scores.</summary>
public sealed record BiasBaseline
{
    public required double ConsistencyValue { get; init; }

    /// <summary>Pair id → 1 (consistent) or 0 (inconsistent) at baseline.</summary>
    public required IReadOnlyDictionary<string, int> ConsistencyPairs { get; init; }

    public required IReadOnlyDictionary<string, BaselineMetric> Metrics { get; init; }
}

/// <summary>One counterfactual pair's measured row for the board; a pair
/// whose legs are missing or unscored is unmeasured and renders as such.</summary>
public sealed record PairRow(string PairId, string Dimension, double? ScoreA, double? ScoreB, bool? Consistent)
{
    public bool Measured => Consistent is not null;
}

/// <summary>One cohort slice of a cohort-sliced metric.</summary>
public sealed record CohortRow(string Cohort, int Cases, double Value);

/// <summary>One cohort-sliced metric: the overall aggregate plus its cohort slices.</summary>
public sealed record MetricRow(string Metric, double Overall, int Cases, IReadOnlyList<CohortRow> Cohorts);

/// <summary>One cohort's refusal behavior over its should-succeed cases.</summary>
public sealed record RefusalRow(string Cohort, int ShouldSucceed, int Refused, double Rate);

/// <summary>One source cohort's share of the run's citations vs the declared expectation.</summary>
public sealed record CitationRow(string Cohort, int Count, double Share, double Expected);

/// <summary>The judged probe run: per-pair consistency, cohort-sliced metrics,
/// refusal asymmetry, citation balance, judge agreement over the
/// human-labeled sample, and the gate's findings.</summary>
public sealed class BiasReport
{
    public required string RunName { get; init; }

    public required IReadOnlyList<BiasPair> Pairs { get; init; }

    public required IReadOnlyList<ProbeCase> Cases { get; init; }

    public required IReadOnlyList<ProbeResult> Results { get; init; }

    /// <summary>Share of measured pairs whose two legs recorded the same
    /// dimension score — rounded to 4 decimals, AwayFromZero.</summary>
    public required double Consistency { get; init; }

    /// <summary>Pair id → 1 or 0, for the measured pairs only.</summary>
    public required IReadOnlyDictionary<string, int> PairConsistency { get; init; }

    public required IReadOnlyList<PairRow> PairRows { get; init; }

    public required IReadOnlyList<MetricRow> Metrics { get; init; }

    public required IReadOnlyList<RefusalRow> Refusal { get; init; }

    /// <summary>Max minus min refusal rate across cohorts over should-succeed cases.</summary>
    public required double RefusalDelta { get; init; }

    public required IReadOnlyList<CitationRow> Citations { get; init; }

    public required int TotalCitations { get; init; }

    public int LabeledSampleSize { get; init; }

    public int JudgeAgrees { get; init; }

    public int JudgeDisagrees { get; init; }

    public int JudgeAbstains { get; init; }

    /// <summary>Case ids (ordinal-sorted) where the judge disagreed with the human label.</summary>
    public required IReadOnlyList<string> JudgeDisagreementCases { get; init; }

    public required IReadOnlyList<BiasFinding> Findings { get; init; }

    public IReadOnlyList<BiasFinding> RenderedFindings => Findings.Take(Cap).ToArray();

    public int Cap { get; init; }

    public int Errors => Findings.Count(f => f.Severity == "error");

    public int Warnings => Findings.Count(f => f.Severity == "warning");

    public double Agreement =>
        LabeledSampleSize == 0 ? 0.0 : Math.Round((double)JudgeAgrees / LabeledSampleSize, 4, MidpointRounding.AwayFromZero);
}

/// <summary>Loads the probe pack's data files with loud errors and
/// line-anchored citations: the raw text is kept so every finding can cite
/// file:line.</summary>
public sealed class BiasFile<T>
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

public static class BiasFiles
{
    public static BiasFile<T> Load<T>(string path, Func<JsonElement, T> parse)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"bias-probe data file not found: {path}");
        }

        var raw = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"bias-probe data file is not valid JSON: {path}: {exception.Message}");
        }

        using (document)
        {
            return new BiasFile<T> { Path = path, Raw = raw, Value = parse(document.RootElement) };
        }
    }

    public static IReadOnlyList<BiasPair> ParsePairs(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("pairs", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("pairs file needs a \"pairs\" array");
        }

        var pairs = new List<BiasPair>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("every pair must be a JSON object");
            }

            var id = RequireString(entry, "id", "pair");
            var dimension = RequireString(entry, "dimension", "pair");
            var markerA = RequireString(entry, "markerA", "pair");
            var markerB = RequireString(entry, "markerB", "pair");
            var input = RequireString(entry, "input", "pair");

            if (string.Equals(markerA, markerB, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"pair '{id}' has identical markers '{markerA}' — a counterfactual pair must vary something");
            }

            if (!input.Contains("{marker}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"pair '{id}' input does not contain the \"{{marker}}\" slot — the generator substitutes the marker there");
            }

            if (!input.Contains("{cohort}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"pair '{id}' input does not contain the \"{{cohort}}\" slot — the generator instantiates the pair per cohort");
            }

            pairs.Add(new BiasPair { Id = id, Dimension = dimension, MarkerA = markerA, MarkerB = markerB, Input = input });
        }

        if (pairs.Count == 0)
        {
            throw new InvalidOperationException("the pair pack is empty — there is nothing to probe");
        }

        foreach (var group in pairs.GroupBy(p => p.Id, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                throw new InvalidOperationException($"the pair pack declares pair '{group.Key}' {group.Count()} times — ambiguous");
            }
        }

        return pairs;
    }

    public static IReadOnlyList<ProbeCase> ParseCases(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cases", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("cases file needs a \"cases\" array");
        }

        var cases = new List<ProbeCase>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("every case must be a JSON object");
            }

            var id = RequireString(entry, "id", "case");
            var cohort = RequireString(entry, "cohort", "case");
            var input = RequireString(entry, "input", "case");
            var expected = RequireString(entry, "expected", "case");

            bool? shouldSucceed = null;
            if (entry.TryGetProperty("shouldSucceed", out var succeedElement))
            {
                if (succeedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidOperationException(
                        $"case '{id}' has a \"shouldSucceed\" that is not a boolean — true (must answer), false (must refuse), or omit it");
                }

                shouldSucceed = succeedElement.ValueKind == JsonValueKind.True;
            }

            var metrics = new List<string>();
            if (entry.TryGetProperty("metrics", out var metricsElement))
            {
                if (metricsElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"case '{id}' needs \"metrics\" as an array of metric ids it rides");
                }

                foreach (var metric in metricsElement.EnumerateArray())
                {
                    if (metric.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(metric.GetString()))
                    {
                        throw new InvalidOperationException(
                            $"case '{id}' has an empty metric name — every metric must be a non-empty string");
                    }

                    metrics.Add(metric.GetString()!);
                }

                foreach (var group in metrics.GroupBy(m => m, StringComparer.Ordinal))
                {
                    if (group.Count() > 1)
                    {
                        throw new InvalidOperationException(
                            $"case '{id}' rides metric '{group.Key}' {group.Count()} times — ambiguous");
                    }
                }
            }

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

            cases.Add(new ProbeCase
            {
                Id = id,
                Cohort = cohort,
                Input = input,
                Expected = expected,
                ShouldSucceed = shouldSucceed,
                Metrics = metrics,
                HumanLabel = human,
            });
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException("the labeled set is empty — there is nothing to evaluate");
        }

        foreach (var group in cases.GroupBy(c => c.Id, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                throw new InvalidOperationException($"the labeled set declares case '{group.Key}' {group.Count()} times — ambiguous");
            }
        }

        return cases;
    }

    public static (string RunName, IReadOnlyList<ProbeResult> Results) ParseResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("run", out var runElement) ||
            runElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("results", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("results file needs a \"run\" name and a \"results\" array");
        }

        var results = new List<ProbeResult>();
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

            if (!entry.TryGetProperty("refused", out var refusedElement) ||
                refusedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidOperationException(
                    $"result '{id}' needs a boolean \"refused\" — whether the system refused, recorded never guessed");
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
                        !BiasProbeConfig.IsValidFraction(score.Value.GetDouble()))
                    {
                        throw new InvalidOperationException(
                            $"result '{id}' has a score for '{score.Name}' that is not a number in [0, 1]");
                    }

                    scores[score.Name] = score.Value.GetDouble();
                }
            }

            var citations = new List<ProbeCitation>();
            if (entry.TryGetProperty("citations", out var citationsElement))
            {
                if (citationsElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"result '{id}' needs \"citations\" as an array of {{ source, cohort }}");
                }

                foreach (var citation in citationsElement.EnumerateArray())
                {
                    if (citation.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException($"result '{id}' has a citation that is not a JSON object");
                    }

                    var source = RequireString(citation, "source", "citation");
                    var cohort = RequireString(citation, "cohort", "citation");
                    citations.Add(new ProbeCitation(source, cohort));
                }
            }

            results.Add(new ProbeResult
            {
                Id = id,
                Predicted = predicted,
                JudgeVerdict = verdict,
                Refused = refusedElement.ValueKind == JsonValueKind.True,
                Scores = scores,
                Citations = citations,
            });
        }

        return (runElement.GetString()!, results);
    }

    public static BiasBaseline ParseBaseline(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("consistency", out var consistencyElement) ||
            consistencyElement.ValueKind != JsonValueKind.Object ||
            !consistencyElement.TryGetProperty("value", out var consistencyValue) ||
            consistencyValue.ValueKind != JsonValueKind.Number ||
            !consistencyElement.TryGetProperty("pairs", out var consistencyPairs) ||
            consistencyPairs.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("metrics", out var metricsElement) ||
            metricsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "baseline file needs a \"consistency\" object of { value, pairs } and a \"metrics\" object");
        }

        var pairOutcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in consistencyPairs.EnumerateObject())
        {
            if (pair.Value.ValueKind != JsonValueKind.Number || !pair.Value.TryGetInt32(out var outcome) ||
                outcome is not (0 or 1))
            {
                throw new InvalidOperationException(
                    $"baseline consistency pair '{pair.Name}' must be 1 (consistent) or 0 (inconsistent)");
            }

            pairOutcomes[pair.Name] = outcome;
        }

        var metrics = new Dictionary<string, BaselineMetric>(StringComparer.Ordinal);
        foreach (var metric in metricsElement.EnumerateObject())
        {
            metrics[metric.Name] = ParseBaselineMetric(metric.Name, metric.Value);
        }

        return new BiasBaseline
        {
            ConsistencyValue = consistencyValue.GetDouble(),
            ConsistencyPairs = pairOutcomes,
            Metrics = metrics,
        };
    }

    private static BaselineMetric ParseBaselineMetric(string name, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("value", out var valueElement) || valueElement.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"baseline metric '{name}' needs a numeric \"value\"");
        }

        var cases = ParseCaseScores(name, element);
        var cohorts = new Dictionary<string, BaselineCohort>(StringComparer.Ordinal);
        if (element.TryGetProperty("cohorts", out var cohortsElement))
        {
            if (cohortsElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"baseline metric '{name}' needs \"cohorts\" as an object of cohort → {{ value, cases }}");
            }

            foreach (var cohort in cohortsElement.EnumerateObject())
            {
                if (cohort.Value.ValueKind != JsonValueKind.Object ||
                    !cohort.Value.TryGetProperty("value", out var cohortValue) ||
                    cohortValue.ValueKind != JsonValueKind.Number)
                {
                    throw new InvalidOperationException(
                        $"baseline metric '{name}' cohort '{cohort.Name}' needs a numeric \"value\"");
                }

                cohorts[cohort.Name] = new BaselineCohort
                {
                    Value = cohortValue.GetDouble(),
                    CaseScores = ParseCaseScores($"{name}/{cohort.Name}", cohort.Value),
                };
            }
        }

        return new BaselineMetric
        {
            Value = valueElement.GetDouble(),
            CaseScores = cases,
            Cohorts = cohorts,
        };
    }

    private static IReadOnlyDictionary<string, double> ParseCaseScores(string name, JsonElement element)
    {
        var caseScores = new Dictionary<string, double>(StringComparer.Ordinal);
        if (element.TryGetProperty("cases", out var casesElement))
        {
            if (casesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"baseline '{name}' needs \"cases\" as an object of case id → score");
            }

            foreach (var pair in casesElement.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.Number)
                {
                    throw new InvalidOperationException($"baseline '{name}' has a non-numeric score for case '{pair.Name}'");
                }

                caseScores[pair.Name] = pair.Value.GetDouble();
            }
        }

        return caseScores;
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
