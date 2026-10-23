namespace Sfg.Toolkit.EvalHarness;

/// <summary>
/// Runs the eval harness (#66): matches a recorded run against the committed
/// labeled set, aggregates per-stage metrics, calibrates the judge against
/// the human-labeled sample, and gates the run against the committed
/// baseline — a regression cites the cases that moved, never just the
/// aggregate. The harness judges recorded outcomes; it never calls a model.
/// </summary>
public static class EvalAnalyzer
{
    public static EvalReport Analyze(EvalHarnessConfig config, string? resultsPath = null)
    {
        var casesFile = EvalFiles.Load(config.CasesPath, EvalFiles.ParseCases);
        var cases = casesFile.Value;
        var resultsFile = EvalFiles.Load(resultsPath ?? config.ResultsPath, EvalFiles.ParseResults);
        var (runName, results) = resultsFile.Value;

        var findings = new List<EvalFinding>();

        // duplicate result ids never pass silently: the second one would be counted twice
        foreach (var group in results.GroupBy(r => r.Id, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                findings.Add(new EvalFinding(
                    "eval/duplicate-result", "error",
                    $"run '{runName}' records case '{group.Key}' {group.Count()} times — the aggregate would double-count it",
                    new[] { resultsFile.Anchor($"\"{group.Key}\"") }));
            }
        }

        var caseById = cases.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in cases.GroupBy(c => c.Id, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                throw new InvalidOperationException(
                    $"the labeled set declares case '{group.Key}' {group.Count()} times — ambiguous");
            }
        }

        // unknown results: a run reporting on a case the suite does not declare
        var resultIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            resultIds.Add(result.Id);
            if (!caseById.ContainsKey(result.Id))
            {
                findings.Add(new EvalFinding(
                    "eval/unknown-result", "error",
                    $"run '{runName}' reports result '{result.Id}', which the labeled set does not declare — an undeclared case cannot be scored",
                    new[] { resultsFile.Anchor($"\"{result.Id}\"") }));
            }
        }

        // dropped cases: the benchmark trap — a case that disappears from the run
        foreach (var missing in cases.Where(c => !resultIds.Contains(c.Id))
                     .Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal))
        {
            findings.Add(new EvalFinding(
                "eval/missing-result", "error",
                $"case '{missing}' has no result in run '{runName}' — a dropped case is a change, never silence",
                new[] { casesFile.Anchor($"\"{missing}\"") }));
        }

        // undeclared metrics in the results: thresholds and baselines key on declared names
        var declaredMetrics = new HashSet<string>(config.Stages.SelectMany(s => s.Metrics), StringComparer.Ordinal);
        foreach (var result in results.Where(r => caseById.ContainsKey(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            foreach (var metric in result.Scores.Keys
                         .Where(m => !declaredMetrics.Contains(m)).OrderBy(m => m, StringComparer.Ordinal))
            {
                findings.Add(new EvalFinding(
                    "eval/unknown-metric", "error",
                    $"result '{result.Id}' carries a score for undeclared metric '{metric}' — declare it on a stage or remove the score",
                    new[] { resultsFile.Anchor($"\"{result.Id}\"") }));
            }
        }

        // per-metric aggregates over the metric's stage cases
        var aggregates = new Dictionary<string, double>(StringComparer.Ordinal);
        var perCase = new Dictionary<string, IReadOnlyList<(string CaseId, double Score)>>(StringComparer.Ordinal);
        foreach (var stage in config.Stages)
        {
            var stageCases = cases.Where(c => c.Stage == stage.Id)
                .Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var stageCasesWithResult = stageCases.Where(resultIds.Contains).ToHashSet(StringComparer.Ordinal);

            foreach (var metric in stage.Metrics)
            {
                var scored = new List<(string CaseId, double Score)>();
                foreach (var caseId in stageCasesWithResult)
                {
                    var result = results.First(r => r.Id == caseId);
                    if (result.Scores.TryGetValue(metric, out var score))
                    {
                        scored.Add((caseId, score));
                    }
                    else
                    {
                        findings.Add(new EvalFinding(
                            "eval/metric-missing-score", "warning",
                            $"case '{caseId}' has no score for metric '{metric}' in run '{runName}' — excluded from the aggregate, never guessed",
                            new[] { resultsFile.Anchor($"\"{caseId}\"") }));
                    }
                }

                var aggregate = scored.Count == 0
                    ? 0.0
                    : Math.Round(scored.Average(s => s.Score), 4, MidpointRounding.AwayFromZero);
                aggregates[metric] = aggregate;
                perCase[metric] = scored.OrderBy(s => s.CaseId, StringComparer.Ordinal).ToArray();
            }
        }

        // judge agreement against the human-labeled sample
        var labeled = cases.Where(c => c.Labeled && resultIds.Contains(c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var agrees = 0;
        var abstains = 0;
        var disagreements = new List<string>();
        foreach (var labeledCase in labeled)
        {
            var result = results.First(r => r.Id == labeledCase.Id);
            if (result.JudgeVerdict == "abstain")
            {
                abstains++;
            }
            else if ((result.JudgeVerdict == "correct") == (labeledCase.HumanLabel == "correct"))
            {
                agrees++;
            }
            else
            {
                disagreements.Add(labeledCase.Id);
            }
        }

        // the gate: thresholds are the floor, the baseline is the trend
        var gateTargets = new Dictionary<string, (double? Threshold, double? Baseline)>(StringComparer.Ordinal);
        EvalFile<EvalBaseline>? baselineFile = null;
        if (File.Exists(config.BaselinePath))
        {
            baselineFile = EvalFiles.Load(config.BaselinePath, EvalFiles.ParseBaseline);
        }
        else
        {
            findings.Add(new EvalFinding(
                "eval/no-baseline", "warning",
                $"no baseline recorded at '{Path.GetFileName(config.BaselinePath)}' — record one with --update-baseline and commit it",
                new[] { config.Anchor("\"baseline\"") }));
        }

        foreach (var metric in config.Stages.SelectMany(s => s.Metrics).OrderBy(m => m, StringComparer.Ordinal))
        {
            var observed = aggregates[metric];
            config.Thresholds.TryGetValue(metric, out var threshold);
            double? baseline = null;
            if (baselineFile is not null)
            {
                if (baselineFile.Value.MetricValues.TryGetValue(metric, out var baselineValue))
                {
                    baseline = baselineValue;
                    var moved = MovedCases(perCase[metric], baselineFile.Value.CaseScores.GetValueOrDefault(metric))
                        .OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    if (observed < baselineValue)
                    {
                        findings.Add(new EvalFinding(
                            "eval/baseline-regression", "error",
                            $"{metric} regressed: observed {observed:0.####} < baseline {baselineValue:0.####} — moved cases: {(moved.Length > 0 ? string.Join(", ", moved.Select(id => $"'{id}'")) : "none scored lower individually (aggregate dilution)")} — cite and fix before merge",
                            EvidenceFor(moved, resultsFile, baselineFile, metric)));
                    }
                    else if (observed > baselineValue)
                    {
                        findings.Add(new EvalFinding(
                            "eval/baseline-improved", "warning",
                            $"{metric} improved: observed {observed:0.####} > baseline {baselineValue:0.####} — re-baseline deliberately with --update-baseline so improvements become the new floor",
                            new[] { baselineFile.Anchor($"\"{metric}\"") }));
                    }
                }
                else
                {
                    findings.Add(new EvalFinding(
                        "eval/baseline-metric-missing", "warning",
                        $"the committed baseline has no entry for metric '{metric}' — the gate compares nothing for it; re-baseline with --update-baseline",
                        new[] { baselineFile.Anchor("\"metrics\"") }));
                }
            }

            if (threshold > 0 && observed < threshold)
            {
                findings.Add(new EvalFinding(
                    "eval/threshold-breach", "error",
                    $"{metric} is below its threshold: observed {observed:0.####} < threshold {threshold:0.####} — the floor the suite declared",
                    new[] { config.Anchor($"\"{metric}\"") }));
            }

            gateTargets[metric] = (threshold > 0 ? threshold : null, baseline);
        }

        var rendered = findings.OrderBy(f => f, EvalFinding.Comparer).ToArray();
        return new EvalReport
        {
            RunName = runName,
            Cases = cases.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(),
            Results = results.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
            Aggregates = aggregates,
            PerCase = perCase,
            GateTargets = gateTargets,
            LabeledSampleSize = labeled.Length,
            JudgeAgrees = agrees,
            JudgeDisagrees = disagreements.Count,
            JudgeAbstains = abstains,
            JudgeDisagreementCases = disagreements.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Findings = rendered,
            Cap = config.MaxFindings,
        };
    }

    /// <summary>Cases whose current score is below their baseline score — the
    /// cases a regression cites. Cases new to the run are not regressions.</summary>
    private static IEnumerable<string> MovedCases(
        IReadOnlyList<(string CaseId, double Score)> current,
        IReadOnlyDictionary<string, double>? baselineCases)
    {
        if (baselineCases is null)
        {
            yield break;
        }

        foreach (var (caseId, score) in current)
        {
            if (baselineCases.TryGetValue(caseId, out var baselineScore) && score < baselineScore)
            {
                yield return caseId;
            }
        }
    }

    private static IReadOnlyList<string> EvidenceFor(
        IReadOnlyList<string> moved,
        EvalFile<(string RunName, IReadOnlyList<EvalResult> Results)> resultsFile,
        EvalFile<EvalBaseline> baselineFile,
        string metric)
    {
        var evidence = new List<string> { baselineFile.Anchor($"\"{metric}\"") };
        evidence.AddRange(moved.Select(id => resultsFile.Anchor($"\"{id}\"")));
        return evidence;
    }
}
