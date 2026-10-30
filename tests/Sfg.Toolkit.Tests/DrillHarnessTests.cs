using System.Text.RegularExpressions;
using Sfg.Toolkit.DrillHarness;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// Resilience drill evidence harness (#81): the control stays silent, every
/// drill/ rule bites with file:line evidence, the clock budgets are inclusive
/// at the boundary, freshness uses the scenario override over the config
/// window, the loud loader never passes a malformed export silently, and the
/// boards are deterministic.
/// </summary>
public sealed class DrillHarnessTests
{
    private static DrillReport Analyze(Action<DrillHarnessFixture>? seed = null)
    {
        var fixture = new DrillHarnessFixture();
        try
        {
            seed?.Invoke(fixture);
            var config = DrillHarnessConfig.Load(fixture.ConfigPath);
            var scenarios = DrillFiles.LoadScenarios(config.ScenariosPath);
            var evidence = DrillFiles.LoadEvidence(config.EvidencePath);
            return DrillAnalyzer.Analyze(config, scenarios, evidence, config.EvidencePath);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static DrillReport AnalyzeWith(Action<DrillHarnessFixture> seed) => Analyze(seed);

    private static DrillFinding Finding(DrillReport report, string rule) =>
        Assert.Single(report.Findings, f => f.Rule == rule);

    private static void AllEvidenceAnchored(DrillFinding finding)
    {
        Assert.NotEmpty(finding.Evidence);
        Assert.All(finding.Evidence, anchor => Assert.Matches(@".+:[0-9]+", anchor));
    }

    // ── the control stays silent

    [Fact]
    public void ControlStaysSilent()
    {
        var report = Analyze();
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Warnings);
        Assert.All(report.Freshness, row => Assert.Equal("current", row.Status));
        Assert.All(report.Clocks, row => Assert.Equal("clean", row.Verdict));
        Assert.All(report.Participation, row => Assert.Equal("recorded", row.Verdict));
    }

    // ── the loader is loud

    [Fact]
    public void MissingConfigIsLoud()
    {
        Assert.Throws<FileNotFoundException>(() => DrillHarnessConfig.Load("/nowhere/drills.json"));
    }

    [Fact]
    public void InvalidConfigJsonIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("broken-config.json", "{ not json");
        Assert.Throws<InvalidOperationException>(() => DrillHarnessConfig.Load(Path.Combine(temp.Root, "broken-config.json")));
    }

    [Fact]
    public void ConfigWithoutNameIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("no-name.json", """
            {
              "judge": "test-judge-v1",
              "scenarios": "scenarios.json",
              "evidence": "drill-evidence.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => DrillHarnessConfig.Load(Path.Combine(temp.Root, "no-name.json")));
    }

    [Fact]
    public void ConfigWithoutJudgeIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("no-judge.json", """
            {
              "name": "support-drills",
              "scenarios": "scenarios.json",
              "evidence": "drill-evidence.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => DrillHarnessConfig.Load(Path.Combine(temp.Root, "no-judge.json")));
    }

    [Fact]
    public void ZeroStalenessWindowIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("zero-stale.json", """
            {
              "name": "support-drills",
              "judge": "test-judge-v1",
              "stalenessDays": 0,
              "scenarios": "scenarios.json",
              "evidence": "drill-evidence.json"
            }
            """);
        Assert.Throws<InvalidOperationException>(() => DrillHarnessConfig.Load(Path.Combine(temp.Root, "zero-stale.json")));
    }

    [Fact]
    public void MissingScenariosFileIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        Assert.Throws<FileNotFoundException>(() =>
            DrillFiles.LoadScenarios(Path.Combine(temp.Root, "no-such-scenarios.json")));
    }

    [Fact]
    public void EmptyScenarioCatalogIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("empty-scenarios.json", "{ \"scenarios\": [] }");
        Assert.Throws<InvalidOperationException>(() =>
            DrillFiles.LoadScenarios(Path.Combine(temp.Root, "empty-scenarios.json")));
    }

    [Fact]
    public void ScenarioWithoutClocksIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("clockless.json", """
            {
              "scenarios": [
                { "id": "r1", "failure": "the paging route silently drops an alarm", "expectedSignals": [ "alarm/paged" ] }
              ]
            }
            """);
        Assert.Throws<InvalidOperationException>(() =>
            DrillFiles.LoadScenarios(Path.Combine(temp.Root, "clockless.json")));
    }

    [Fact]
    public void DuplicateScenarioIdsAreLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("dup-scenarios.json", """
            {
              "scenarios": [
                { "id": "r1", "failure": "one", "expectedSignals": [ "a" ], "clocks": { "alertSeconds": 60, "rtoSeconds": 600, "rpoSeconds": 120 } },
                { "id": "r1", "failure": "two", "expectedSignals": [ "b" ], "clocks": { "alertSeconds": 60, "rtoSeconds": 600, "rpoSeconds": 120 } }
              ]
            }
            """);
        Assert.Throws<InvalidOperationException>(() =>
            DrillFiles.LoadScenarios(Path.Combine(temp.Root, "dup-scenarios.json")));
    }

    [Fact]
    public void EvidenceWithoutAsOfIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("no-asof.json", """
            {
              "drill": "test-drill-1",
              "records": [ ]
            }
            """);
        Assert.Throws<InvalidOperationException>(() =>
            DrillFiles.LoadEvidence(Path.Combine(temp.Root, "no-asof.json")));
    }

    [Fact]
    public void MalformedTimestampIsLoud()
    {
        using var temp = new DrillHarnessFixture();
        temp.Write("bad-time.json", """
            {
              "drill": "test-drill-1",
              "asOf": "2026-10-15T00:00:00Z",
              "records": [
                { "scenario": "r1", "startedAt": "yesterday", "alertedAt": "2026-10-10T08:00:40Z", "restoredAt": "2026-10-10T08:08:00Z", "signals": [ ] }
              ]
            }
            """);
        Assert.Throws<InvalidOperationException>(() =>
            DrillFiles.LoadEvidence(Path.Combine(temp.Root, "bad-time.json")));
    }

    // ── the clocks bite, and budgets met at the boundary are met

    [Fact]
    public void LateAlertBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r1", DrillHarnessFixture.LateR1))));
        var finding = Finding(report, "drill/late-alert");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("alerted 120s after injection", finding.Message);
        Assert.Contains("over the 60s alert clock", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void AlertAtTheClockBoundaryIsMet()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r1", DrillHarnessFixture.AtClockR1))));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void RestoreOverRtoBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r2", DrillHarnessFixture.OverRtoR2))));
        var finding = Finding(report, "drill/restore-over-rto");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("restored 960s after injection", finding.Message);
        Assert.Contains("over the 900s RTO", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void RestoreAtTheRtoBoundaryIsMet()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r2", DrillHarnessFixture.AtRtoR2))));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void RpoBreachBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r3", DrillHarnessFixture.RpoR3))));
        var finding = Finding(report, "drill/rpo-breached");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("restored 660s after its last recoverable data", finding.Message);
        Assert.Contains("over the 600s RPO", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void RpoAtTheBoundaryIsMet()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r3", DrillHarnessFixture.AtRpoR3))));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void AlertNeverFiredBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r1", DrillHarnessFixture.NoAlertR1))));
        var finding = Finding(report, "drill/alert-never-fired");
        Assert.Equal("error", finding.Severity);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void RestoreNeverHappenedBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r2", DrillHarnessFixture.NoRestoreR2))));
        var finding = Finding(report, "drill/restore-never-happened");
        Assert.Equal("error", finding.Severity);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void UnverifiableRpoWarns()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r3", DrillHarnessFixture.NoLastRecoverableR3))));
        var finding = Finding(report, "drill/rpo-unrecorded");
        Assert.Equal("warning", finding.Severity);
        Assert.Equal(0, report.Errors);
        Assert.Equal(1, report.Warnings);
        AllEvidenceAnchored(finding);
    }

    // ── signals, participation, and record hygiene

    [Fact]
    public void MissingExpectedSignalBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r2", DrillHarnessFixture.SignalMissR2))));
        var finding = Finding(report, "drill/signal-missing");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'cache/evicted'", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void UnexpectedSignalWarns()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r1", DrillHarnessFixture.UnknownSignalR1))));
        var finding = Finding(report, "drill/unknown-signal");
        Assert.Equal("warning", finding.Severity);
        Assert.Equal(0, report.Errors);
        Assert.Equal(1, report.Warnings);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void UnrecordedParticipantsBite()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r1", DrillHarnessFixture.NoParticipantsR1))));
        var finding = Finding(report, "drill/participants-unrecorded");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("recorded no participants", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void UnrecordedVersionsBite()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r2", DrillHarnessFixture.NoVersionsR2))));
        var finding = Finding(report, "drill/participants-unrecorded");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("recorded no component versions", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void UndeclaredScenarioInEvidenceBites()
    {
        var seeded = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.AppendRecord(DrillHarnessFixture.BaseRecords, DrillHarnessFixture.UndeclaredR9))));
        var finding = Finding(seeded, "drill/evidence-undeclared");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r9'", finding.Message);
        Assert.Equal(1, seeded.Errors);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void DuplicateRecordBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.AppendRecord(DrillHarnessFixture.BaseRecords, DrillHarnessFixture.DuplicateR1))));
        var finding = Finding(report, "drill/duplicate-drill");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("recorded twice", finding.Message);
        Assert.Equal(1, report.Errors);
        AllEvidenceAnchored(finding);
    }

    // ── freshness: a paper plan and a rehearsed one must not look alike

    [Fact]
    public void NeverDrilledScenarioBites()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithoutRecord(DrillHarnessFixture.BaseRecords, "r3"))));
        var finding = Finding(report, "drill/never-drilled");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r3' is in the catalog but was never drilled", finding.Message);
        Assert.Equal(1, report.Errors);
        var row = Assert.Single(report.Freshness, r => r.Scenario == "r3");
        Assert.Equal("never", row.Status);
        AllEvidenceAnchored(finding);
    }

    [Fact]
    public void StaleScenarioWarnsOnTheScenarioWindow()
    {
        var report = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(f.WithRecord(DrillHarnessFixture.BaseRecords, "r3", DrillHarnessFixture.StaleR3))));
        var finding = Finding(report, "drill/stale-scenario");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("136 days before 2026-10-15", finding.Message);
        Assert.Contains("30-day freshness window", finding.Message);
        Assert.Equal(0, report.Errors);
        Assert.Equal(1, report.Warnings);
        var row = Assert.Single(report.Freshness, r => r.Scenario == "r3");
        Assert.Equal("stale", row.Status);
        Assert.Equal(30, row.StalenessDays);
        AllEvidenceAnchored(finding);
    }

    // ── the boards are deterministic and carry the generator

    [Fact]
    public void BoardsAreDeterministic()
    {
        var first = Render();
        var second = Render();
        Assert.Equal(first, second);
    }

    private static (string Markdown, string Json) Render()
    {
        var fixture = new DrillHarnessFixture();
        try
        {
            var config = DrillHarnessConfig.Load(fixture.ConfigPath);
            var scenarios = DrillFiles.LoadScenarios(config.ScenariosPath);
            var evidence = DrillFiles.LoadEvidence(config.EvidencePath);
            var report = DrillAnalyzer.Analyze(config, scenarios, evidence, config.EvidencePath);
            return (DrillHarnessRenderer.Markdown(config, report), DrillHarnessRenderer.Json(config, report));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void JsonCarriesGeneratorAndCounts()
    {
        var (markdown, json) = Render();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("sfg drills 0.11.0", document.RootElement.GetProperty("generator").GetString());
        Assert.Equal("support-drills", document.RootElement.GetProperty("suite").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("freshness").EnumerateObject().Count());
        Assert.Equal(3, document.RootElement.GetProperty("clocks").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("errors").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("warnings").GetInt32());
        Assert.Contains("✅ Clean run:", markdown);
        Assert.Contains("sfg drills 0.11.0", markdown);
    }

    [Fact]
    public void EveryFindingCitesLineAnchors()
    {
        // Every evidence anchor of every finding of a seeded run is file:line.
        var seeded = AnalyzeWith(f => f.Write("drill-evidence.json", f.Compose(
            f.WithRecord(f.WithRecord(DrillHarnessFixture.BaseRecords, "r1", DrillHarnessFixture.LateR1), "r2", DrillHarnessFixture.NoVersionsR2))));
        Assert.Equal(2, seeded.Findings.Count);
        Assert.All(seeded.Findings, f => Assert.All(f.Evidence, anchor => Assert.Matches(@".+:[0-9]+", anchor)));
    }
}
