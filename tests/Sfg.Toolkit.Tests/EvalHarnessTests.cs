using System.Text.RegularExpressions;
using Sfg.Toolkit.EvalHarness;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// LLM eval harness (#66): config validation is loud, the gate compares
/// against both the declared thresholds and the committed baseline, a
/// regression cites the cases that moved (never just the aggregate), judge
/// agreement is calibrated against the human-labeled sample (abstains never
/// count), and all rendering is deterministic with file:line evidence.
/// </summary>
public sealed class EvalHarnessTests : IDisposable
{
    private static readonly Regex FileLine = new(@":\d+$", RegexOptions.Compiled);

    private readonly EvalAppFixture _app = new();

    public void Dispose() => _app.Dispose();

    private EvalHarnessConfig LoadConfig(string fileName = "eval.json") =>
        EvalHarnessConfig.Load(Path.Combine(_app.Root, fileName));

    private EvalReport Analyze(string? resultsFile = null, string? configFile = null) =>
        EvalAnalyzer.Analyze(
            LoadConfig(configFile ?? "eval.json"),
            resultsFile is null ? null : Path.Combine(_app.Root, resultsFile));

    // --- config validation: loud, never a guess -------------------------------

    [Fact]
    public void Config_rejects_duplicate_stage_ids()
    {
        _app.Write("bad.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [
                { "id": "retrieval", "metrics": ["recallAt3"] },
                { "id": "retrieval", "metrics": ["precisionAt1"] }
              ],
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => LoadConfig("bad.json"));
        Assert.Contains("duplicate stage id 'retrieval'", error.Message);
    }

    [Fact]
    public void Config_rejects_duplicate_metrics_across_stages()
    {
        _app.Write("bad.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [
                { "id": "retrieval", "metrics": ["recallAt3"] },
                { "id": "generation", "metrics": ["recallAt3"] }
              ],
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => LoadConfig("bad.json"));
        Assert.Contains("duplicate metric 'recallAt3'", error.Message);
    }

    [Fact]
    public void Config_rejects_a_threshold_for_an_undeclared_metric()
    {
        _app.Write("bad.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [ { "id": "retrieval", "metrics": ["recallAt3"] } ],
              "thresholds": { "answerRelevance": 0.9 },
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => LoadConfig("bad.json"));
        Assert.Contains("threshold names undeclared metric 'answerRelevance'", error.Message);
    }

    [Fact]
    public void Config_rejects_an_empty_stages_array()
    {
        _app.Write("bad.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [],
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => LoadConfig("bad.json"));
        Assert.Contains("declares no stages", error.Message);
    }

    [Fact]
    public void Analyze_fails_loudly_when_the_cases_file_is_missing()
    {
        _app.Write("nolist.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [ { "id": "retrieval", "metrics": ["recallAt3"] } ],
              "cases": "not-there.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
        var error = Assert.Throws<FileNotFoundException>(() => Analyze(configFile: "nolist.json"));
        Assert.Contains("not-there.json", error.Message);
    }

    // --- the control run: silent when everything holds ------------------------

    [Fact]
    public void Good_run_produces_no_findings()
    {
        var report = Analyze();
        Assert.Empty(report.Findings);
        Assert.Equal(0.8333, report.Aggregates["recallAt3"]);
        Assert.Equal(0.9, report.Aggregates["faithfulness"]);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Warnings);
    }

    // --- the gate bites: every failure mode is an explicit finding -------------

    [Fact]
    public void Unknown_results_are_flagged_with_file_line_evidence()
    {
        _app.Write("results.unknown.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } },
                { "id": "r99", "predicted": "docs/stray.md", "judge": "correct", "scores": { "recallAt3": 1.0 } }
              ]
            }
            """);
        var report = Analyze("results.unknown.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/unknown-result");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r99'", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
        Assert.Contains(finding.Evidence, e => e.StartsWith("results.unknown.json:"));
    }

    [Fact]
    public void A_dropped_case_is_a_change_never_silence()
    {
        _app.Write("results.missing.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.missing.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/missing-result");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r3'", finding.Message);
        // a dropped case cites the labeled set, not the run that cannot contain it
        Assert.Contains(finding.Evidence, e => e.StartsWith("cases.json:"));
    }

    [Fact]
    public void Duplicate_results_never_pass_silently()
    {
        _app.Write("results.duplicate.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.duplicate.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/duplicate-result");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r3' 2 times", finding.Message);
    }

    [Fact]
    public void Scores_for_undeclared_metrics_are_flagged()
    {
        _app.Write("results.stray.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0, "precisionAt1": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.stray.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/unknown-metric");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'precisionAt1'", finding.Message);
    }

    [Fact]
    public void A_case_missing_a_metric_score_is_a_warning_never_a_guess()
    {
        _app.Write("results.unscored.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": {} },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.unscored.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/metric-missing-score");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("'g2'", finding.Message);
        Assert.Contains("'faithfulness'", finding.Message);
        // the aggregate excludes the unscored case, it never guesses: (1.0 + 0.8) / 2 = 0.9
        Assert.Equal(0.9, report.Aggregates["faithfulness"]);
    }

    [Fact]
    public void A_threshold_breach_cites_the_config_line()
    {
        _app.Write("strict.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [
                { "id": "retrieval", "metrics": ["recallAt3"] },
                { "id": "generation", "metrics": ["faithfulness"] }
              ],
              "thresholds": {
                "recallAt3": 0.9,
                "faithfulness": 0.9
              },
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "baseline.json"
            }
            """);
        var report = Analyze(configFile: "strict.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/threshold-breach");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("recallAt3", finding.Message);
        Assert.Contains("0.8333", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
        Assert.Contains(finding.Evidence, e => e.StartsWith("strict.json:"));
    }

    [Fact]
    public void A_regression_cites_the_cases_that_moved()
    {
        _app.Write("results.regressed.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "(no hit)", "judge": "incorrect", "scores": { "recallAt3": 0.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.regressed.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/baseline-regression");
        Assert.Equal("error", finding.Severity);
        // the moved case is named, never just the aggregate
        Assert.Contains("moved cases: 'r2'", finding.Message);
        Assert.Contains("0.5", finding.Message);
        Assert.Contains("0.8333", finding.Message);
        // evidence: the baseline entry plus the moved case's result line
        Assert.Contains(finding.Evidence, e => e.StartsWith("baseline.json:"));
        Assert.Contains(finding.Evidence, e => e.StartsWith("results.regressed.json:"));
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
    }

    [Fact]
    public void An_improvement_is_a_warning_and_re_baselining_is_deliberate()
    {
        _app.Write("results.improved.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.improved.json");
        Assert.Equal(2, report.Findings.Count(f => f.Rule == "eval/baseline-improved"));
        Assert.All(report.Findings, f => Assert.Equal("warning", f.Severity));
        Assert.Equal(0, report.Errors);
    }

    [Fact]
    public void A_missing_baseline_warns_and_gates_on_thresholds_only()
    {
        _app.Write("nobaseline.json", """
            {
              "name": "docs-eval-test",
              "judge": "test-judge-v1",
              "stages": [
                { "id": "retrieval", "metrics": ["recallAt3"] },
                { "id": "generation", "metrics": ["faithfulness"] }
              ],
              "thresholds": {
                "recallAt3": 0.8,
                "faithfulness": 0.9
              },
              "cases": "cases.json",
              "results": "results.json",
              "baseline": "not-committed.json"
            }
            """);
        var report = Analyze(configFile: "nobaseline.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "eval/no-baseline");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("not-committed.json", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(FileLine, e));
        // the run still passes the threshold floor
        Assert.Equal(0, report.Errors);
    }

    // --- judge calibration against the human-labeled sample -------------------

    [Fact]
    public void Judge_agreement_is_reported_over_the_human_labeled_sample()
    {
        var report = Analyze();
        Assert.Equal(4, report.LabeledSampleSize);
        Assert.Equal(4, report.JudgeAgrees);
        Assert.Equal(0, report.JudgeDisagrees);
        Assert.Equal(1.0, report.Agreement);
        Assert.Empty(report.JudgeDisagreementCases);
    }

    [Fact]
    public void Abstains_never_count_as_agreement()
    {
        _app.Write("results.abstain.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "abstain", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.abstain.json");
        Assert.Equal(4, report.LabeledSampleSize);
        Assert.Equal(3, report.JudgeAgrees);
        Assert.Equal(1, report.JudgeAbstains);
        Assert.Equal(0.75, report.Agreement);
        Assert.Equal(0, report.JudgeDisagrees);
    }

    [Fact]
    public void Judge_disagreements_name_the_cases()
    {
        _app.Write("results.disagree.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/other.md", "judge": "incorrect", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } }
              ]
            }
            """);
        var report = Analyze("results.disagree.json");
        Assert.Equal(1, report.JudgeDisagrees);
        Assert.Equal(new[] { "r1" }, report.JudgeDisagreementCases);
        Assert.Equal(0.75, report.Agreement);
    }

    // --- determinism and citations ---------------------------------------------

    [Fact]
    public void Markdown_cites_file_line_for_every_finding()
    {
        _app.Write("results.unknown.json", """
            {
              "run": "assistant-v3",
              "results": [
                { "id": "r1", "predicted": "docs/rotation.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r2", "predicted": "docs/outbox.md", "judge": "correct", "scores": { "recallAt3": 1.0 } },
                { "id": "r3", "predicted": "docs/plugins.md", "judge": "correct", "scores": { "recallAt3": 0.5 } },
                { "id": "g1", "predicted": "summary: rotate via re-wrap", "judge": "correct", "scores": { "faithfulness": 1.0 } },
                { "id": "g2", "predicted": "summary: outbox before dual-write", "judge": "correct", "scores": { "faithfulness": 0.9 } },
                { "id": "g3", "predicted": "summary: rebind per session", "judge": "incorrect", "scores": { "faithfulness": 0.8 } },
                { "id": "r99", "predicted": "docs/stray.md", "judge": "correct", "scores": { "recallAt3": 1.0 } }
              ]
            }
            """);
        var config = LoadConfig();
        var report = Analyze("results.unknown.json");
        var markdown = EvalRenderer.Markdown(config, report);
        Assert.Contains("eval/unknown-result", markdown);
        Assert.Matches(new Regex(@":[0-9]+`", RegexOptions.None), markdown);
    }

    [Fact]
    public void Renderers_are_deterministic()
    {
        var config = LoadConfig();
        var first = Analyze();
        var second = Analyze();
        Assert.Equal(EvalRenderer.Markdown(config, first), EvalRenderer.Markdown(config, second));
        Assert.Equal(EvalRenderer.Json(config, first), EvalRenderer.Json(config, second));
        Assert.Equal(EvalRenderer.BaselineJson(config, first), EvalRenderer.BaselineJson(config, second));
    }

    [Fact]
    public void Update_baseline_writes_the_aggregates_and_per_case_scores()
    {
        var config = LoadConfig();
        var report = Analyze();
        var baseline = EvalRenderer.BaselineJson(config, report);
        Assert.Contains("\"recallAt3\": { \"value\": 0.8333", baseline);
        Assert.Contains("\"r3\": 0.5", baseline);
        Assert.Contains("\"faithfulness\": { \"value\": 0.9", baseline);
        // and re-running the tool against its own baseline is steady, never a regression
        _app.Write("baseline.json", baseline);
        var reread = Analyze();
        Assert.DoesNotContain(reread.Findings, f => f.Rule is "eval/baseline-regression" or "eval/baseline-improved");
    }
}
