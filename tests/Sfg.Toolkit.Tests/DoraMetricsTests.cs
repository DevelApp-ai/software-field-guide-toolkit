using Sfg.Toolkit.DoraMetrics;
using System.Text.RegularExpressions;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// DORA four-key extractor (#68): the loader is loud on a malformed export,
/// every join the four keys depend on is verified against the recorded data
/// (deployment → change → build, failure → restore evidence), a re-run
/// mislabeled as a new deployment is caught by construction and excluded from
/// the counts, the metrics are hand-computable trends against the versioned
/// definitions file, and all rendering is deterministic with file:line
/// evidence.
/// </summary>
public sealed class DoraMetricsTests : IDisposable
{
    private static readonly Regex FileLine = new(@":[0-9]+`", RegexOptions.Compiled);

    private static readonly Regex Anchor = new(@":[0-9]+$", RegexOptions.Compiled);

    private readonly DoraAppFixture _app = new();

    public void Dispose() => _app.Dispose();

    private void WriteVariant(string name, string deployments, string changes, string builds, string failures) =>
        _app.Write(name, _app.Compose(deployments, changes, builds, failures));

    // --- the loader is loud: the export format is the contract ---------------

    [Fact]
    public void Loader_rejects_a_missing_export()
    {
        var config = new DoraConfig { Root = _app.Root, EventsOverride = Path.Combine(_app.Root, "absent.json") };
        var error = Assert.Throws<FileNotFoundException>(() => DoraAnalyzer.Load(config));
        Assert.Contains("dora events export not found", error.Message);
        Assert.Contains("never calls an API live", error.Message);
    }

    [Fact]
    public void Loader_rejects_a_missing_array()
    {
        _app.Write("partial.json", $$"""
            { "source": { "repository": "example/api" }, "deployments": [], "changes": [], "failures": [] }
            """);
        var config = new DoraConfig { Root = _app.Root, EventsOverride = Path.Combine(_app.Root, "partial.json") };
        var error = Assert.Throws<InvalidOperationException>(() => DoraAnalyzer.Load(config));
        Assert.Contains("\"builds\"", error.Message);
        Assert.Contains("array", error.Message);
    }

    [Fact]
    public void Loader_rejects_invalid_timestamps()
    {
        WriteVariant("badtime.json",
            DoraAppFixture.BaseDeployments.Replace("2026-06-01T10:00:00Z", "06/01/2026 10:00"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Extract("badtime.json"));
        Assert.Contains("deployment 'r1' has an invalid \"deployedAt\"", error.Message);
        Assert.Contains("ISO 8601 UTC", error.Message);
    }

    [Fact]
    public void Loader_rejects_duplicate_deployment_ids()
    {
        WriteVariant("dupe.json",
            DoraAppFixture.BaseDeployments + "\n" + DoraAppFixture.ReRunDeployment.Replace("r1-rerun", "r2").Replace("2026-06-01T11:00:00Z", "2026-06-02T11:00:00Z"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Extract("dupe.json"));
        Assert.Contains("duplicate deployment id 'r2'", error.Message);
    }

    [Fact]
    public void Loader_rejects_non_integer_prs()
    {
        WriteVariant("badpr.json",
            DoraAppFixture.BaseDeployments.Replace("\"prs\": [1]", "\"prs\": [\"one\"]"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var error = Assert.Throws<InvalidOperationException>(() => _app.Extract("badpr.json"));
        Assert.Contains("deployment 'r1' has a non-integer \"prs\" entry", error.Message);
    }

    [Fact]
    public void Config_rejects_an_unknown_period()
    {
        _app.Write("dora.json", """{ "period": "quarter" }""");
        var error = Assert.Throws<InvalidOperationException>(
            () => DoraConfig.Load(Path.Combine(_app.Root, "dora.json"), _app.Root, eventsOverride: null));
        Assert.Contains("\"period\" must be day, week, or month", error.Message);
    }

    [Fact]
    public void Config_file_period_is_loaded()
    {
        _app.Write("dora.json", """{ "period": "month" }""");
        var config = DoraConfig.Load(Path.Combine(_app.Root, "dora.json"), _app.Root, eventsOverride: null);
        Assert.Equal(DoraPeriod.Month, config.Period);
        Assert.Equal("dora-events.json", config.EventsPath);
    }

    // --- the control stays silent, with hand-computable metrics ---------------

    [Fact]
    public void Control_export_is_clean_with_every_join_verified()
    {
        var report = _app.Extract();
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Warnings);
        Assert.All(report.Joins, join => Assert.True(join.Clean, $"join not clean: {join.Join}"));
        Assert.Equal(4, report.Deployments.Count);
        Assert.Equal(4, report.Changes.Count);
        Assert.Equal(6, report.Builds.Count); // the two ci runs on sha 1111… deduplicate to one attempt
        Assert.Equal(6, report.Builds.Count); // 7 recorded runs — the two ci runs on sha 1111… deduplicate to one attempt
        Assert.Equal("example/api", report.Repository);
    }

    [Fact]
    public void Series_week_trends_the_four_keys()
    {
        var series = _app.Extract().Series;
        Assert.Equal(2, series.Count);

        var week1 = Assert.Single(series, row => row.Period == "2026-06-01");
        Assert.Equal(3, week1.Deployments);
        Assert.Equal(3, week1.Changes);
        Assert.Equal(1.5, week1.LeadTimeHours); // median of 1.0, 1.5, 26.0
        Assert.Equal(33.3, week1.ChangeFailureRate); // 1 verified failure over 3 deployments
        Assert.Equal(2.5, week1.MttrHours); // 11:00 → 13:30

        var week2 = Assert.Single(series, row => row.Period == "2026-06-08");
        Assert.Equal(1, week2.Deployments);
        Assert.Equal(1, week2.Changes);
        Assert.Equal(0.5, week2.LeadTimeHours);
        Assert.Equal(0.0, week2.ChangeFailureRate);
        Assert.Null(week2.MttrHours);
    }

    [Fact]
    public void Series_month_and_day_period_keys()
    {
        var month = Assert.Single(_app.Extract(period: DoraPeriod.Month).Series);
        Assert.Equal("2026-06", month.Period);
        Assert.Equal(4, month.Deployments);
        Assert.Equal(4, month.Changes);
        Assert.Equal(1.3, month.LeadTimeHours); // median of 0.5, 1.0, 1.5, 26.0 = 1.25, rounded half away from zero
        Assert.Equal(25.0, month.ChangeFailureRate); // 1 failure over 4 deployments
        Assert.Equal(2.5, month.MttrHours);

        var day = _app.Extract(period: DoraPeriod.Day).Series;
        Assert.Equal(3, day.Count);
        Assert.Equal("2026-06-01", day[0].Period);
        Assert.Equal(2, day[0].Deployments);
        Assert.Equal(50.0, day[0].ChangeFailureRate); // the failure was detected on a two-deployment day
        Assert.Equal("2026-06-08", day[2].Period);
    }

    // --- the seeded re-run is caught by construction (#68 done-when) -----------

    [Fact]
    public void Re_run_mislabeled_as_a_new_deployment_is_caught_and_excluded()
    {
        WriteVariant("rerun.json",
            DoraAppFixture.BaseDeployments + "\n" + DoraAppFixture.ReRunDeployment,
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var report = _app.Extract("rerun.json");

        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/re-run-mislabeled");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r1-rerun'", finding.Message);
        Assert.Contains("already deployed by 'r1'", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(Anchor, e));

        Assert.Equal("r1-rerun", Assert.Single(report.ExcludedDeployments));
        Assert.Equal(1, report.Errors);

        // the counts are unchanged by the re-run: 3 deployments and 3 changes in
        // week one — the re-run is not a fourth deployment
        var week1 = Assert.Single(report.Series, row => row.Period == "2026-06-01");
        Assert.Equal(3, week1.Deployments);
        Assert.Equal(3, week1.Changes);
        Assert.Equal(1.5, week1.LeadTimeHours);
    }

    // --- every join bites on its seeded failure mode ---------------------------

    [Fact]
    public void Dangling_join_bites()
    {
        WriteVariant("dangling.json",
            DoraAppFixture.BaseDeployments.Replace("\"prs\": [4]", "\"prs\": [4, 99]"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var report = _app.Extract("dangling.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/dangling-join");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("PR #99", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(Anchor, e));
        var join = Assert.Single(report.Joins, j => j.Join == "deployment → change");
        Assert.Equal(4, join.Verified);
        Assert.Equal(5, join.Total);
    }

    [Fact]
    public void Temporal_join_violation_bites()
    {
        WriteVariant("temporal.json",
            DoraAppFixture.BaseDeployments.Replace("2026-06-08T10:00:00Z", "2026-06-08T09:00:00Z"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var report = _app.Extract("temporal.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/temporal-join-violation");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("PR #4", finding.Message);
        Assert.Contains("a cause cannot land after its effect", finding.Message);
    }

    [Fact]
    public void Unbuilt_deployment_bites()
    {
        WriteVariant("unbuilt.json",
            DoraAppFixture.BaseDeployments,
            DoraAppFixture.BaseChanges,
            DoraAppFixture.BaseBuilds.Replace("\n{ \"run\": 103, \"workflow\": \"ci\", \"headSha\": \"6666666666666666666666666666666666666666\", \"conclusion\": \"success\", \"completedAt\": \"2026-06-01T10:05:00Z\" }", string.Empty),
            DoraAppFixture.BaseFailures);
        var report = _app.Extract("unbuilt.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/unbuilt-deployment");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'r1'", finding.Message);
        Assert.Equal(1, report.Errors);
        Assert.Equal(0, report.Warnings); // change #1's merge sha is still observed — no warning
    }

    [Fact]
    public void Change_without_build_warns()
    {
        WriteVariant("nobuild.json",
            DoraAppFixture.BaseDeployments,
            DoraAppFixture.BaseChanges,
            DoraAppFixture.BaseBuilds.Replace("\n{ \"run\": 106, \"workflow\": \"ci\", \"headSha\": \"4444444444444444444444444444444444444444\", \"conclusion\": \"success\", \"completedAt\": \"2026-06-08T09:40:00Z\" }", string.Empty),
            DoraAppFixture.BaseFailures);
        var report = _app.Extract("nobuild.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/change-without-build");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("PR #4", finding.Message);
        Assert.Equal(0, report.Errors);
    }

    [Fact]
    public void Unshipped_change_warns()
    {
        WriteVariant("unshipped.json",
            DoraAppFixture.BaseDeployments.Replace("\"prs\": [4]", "\"prs\": []"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds, DoraAppFixture.BaseFailures);
        var report = _app.Extract("unshipped.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/unshipped-change");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("PR #4", finding.Message);
    }

    // --- the failure taxonomy is checked, never trusted ------------------------

    [Fact]
    public void Unknown_failure_mode_bites_and_does_not_count()
    {
        WriteVariant("unknown.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"mode\": \"hotfix\"", "\"mode\": \"redeploy\""));
        var report = _app.Extract("unknown.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/unknown-failure-mode");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'redeploy'", finding.Message);
        Assert.Equal(1, report.Errors);

        // an unclassifiable restore is not a restore yet: the failure drops out
        // of the failure-rate and restore-time keys
        var week1 = Assert.Single(report.Series, row => row.Period == "2026-06-01");
        Assert.Equal(0.0, week1.ChangeFailureRate);
        Assert.Null(week1.MttrHours);
    }

    [Fact]
    public void Unevidenced_hotfix_bites()
    {
        WriteVariant("unevidenced.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"hotfixPr\": 3", "\"hotfixPr\": 999"));
        var report = _app.Extract("unevidenced.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/unevidenced-failure");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("#999 does not resolve to a change merged inside the incident window", finding.Message);
    }

    [Fact]
    public void Revert_evidence_must_merge_inside_the_incident_window()
    {
        WriteVariant("revert-outside.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"mode\": \"hotfix\"", "\"mode\": \"revert\"").Replace("\"hotfixPr\": 3", "\"revertPr\": 1"));
        var outside = _app.Extract("revert-outside.json");
        var finding = Assert.Single(outside.Findings, f => f.Rule == "dora/unevidenced-failure");
        Assert.Contains("revertPr #1", finding.Message);

        WriteVariant("revert-inside.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"mode\": \"hotfix\"", "\"mode\": \"revert\"").Replace("\"hotfixPr\": 3", "\"revertPr\": 2"));
        var inside = _app.Extract("revert-inside.json"); // PR #2 merged 08:00, before detection 11:00 — still outside
        Assert.NotEmpty(inside.Findings);

        WriteVariant("revert-clean.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"mode\": \"hotfix\"", "\"mode\": \"revert\"")
                .Replace("\"hotfixPr\": 3", "\"revertPr\": 3"));
        var clean = _app.Extract("revert-clean.json"); // PR #3 merged 2026-06-01 12:00, inside [11:00, 13:30]
        Assert.Equal(0, clean.Errors);
    }

    [Fact]
    public void Rollback_evidence_must_target_an_earlier_different_deployment()
    {
        // rollbackTo names the failing deployment itself — same sha, not a rollback
        WriteVariant("rollback-same.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"mode\": \"hotfix\"", "\"mode\": \"rollback\"").Replace("\"evidence\": { \"hotfixPr\": 3 }", "\"evidence\": { \"rollbackTo\": \"r1\" }"));
        var same = _app.Extract("rollback-same.json");
        Assert.Contains(same.Findings, f => f.Rule == "dora/unevidenced-failure");

        // rollbackTo r1 (different target sha, deployed before r4) checks out;
        // the failure is detected in week two, after r4 shipped
        WriteVariant("rollback-clean.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"deployment\": \"r1\"", "\"deployment\": \"r4\"")
                .Replace("\"mode\": \"hotfix\"", "\"mode\": \"rollback\"")
                .Replace("\"evidence\": { \"hotfixPr\": 3 }", "\"evidence\": { \"rollbackTo\": \"r1\" }")
                .Replace("\"detectedAt\": \"2026-06-01T11:00:00Z\"", "\"detectedAt\": \"2026-06-08T10:30:00Z\"")
                .Replace("\"restoredAt\": \"2026-06-01T13:30:00Z\"", "\"restoredAt\": \"2026-06-08T13:00:00Z\""));
        var clean = _app.Extract("rollback-clean.json");
        Assert.Equal(0, clean.Errors);
        var week2 = Assert.Single(clean.Series, row => row.Period == "2026-06-08");
        Assert.Equal(100.0, week2.ChangeFailureRate);
    }

    [Fact]
    public void Dangling_failure_join_bites()
    {
        WriteVariant("dangling-failure.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"deployment\": \"r1\"", "\"deployment\": \"rx\""));
        var report = _app.Extract("dangling-failure.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/dangling-failure-join");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("'rx'", finding.Message);
    }

    [Fact]
    public void Restore_before_detection_bites()
    {
        WriteVariant("restore-first.json",
            DoraAppFixture.BaseDeployments, DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures
                .Replace("\"detectedAt\": \"2026-06-01T11:00:00Z\"", "\"detectedAt\": \"2026-06-01T13:30:00Z\"")
                .Replace("\"restoredAt\": \"2026-06-01T13:30:00Z\"", "\"restoredAt\": \"2026-06-01T11:00:00Z\""));
        var report = _app.Extract("restore-first.json");
        var finding = Assert.Single(report.Findings, f => f.Rule == "dora/restore-before-detection");
        Assert.Equal("error", finding.Severity);
    }

    // --- rendering: deterministic, cited, errors-first --------------------------

    [Fact]
    public void Markdown_cites_file_line_and_ranks_errors_first()
    {
        WriteVariant("mixed.json",
            DoraAppFixture.BaseDeployments.Replace("\"prs\": [4]", "\"prs\": []"),
            DoraAppFixture.BaseChanges, DoraAppFixture.BaseBuilds,
            DoraAppFixture.BaseFailures.Replace("\"hotfixPr\": 3", "\"hotfixPr\": 999"));
        var report = _app.Extract("mixed.json");
        Assert.Equal(1, report.Errors);
        Assert.Equal(1, report.Warnings);

        var config = new DoraConfig { Root = _app.Root, EventsOverride = Path.Combine(_app.Root, "mixed.json") };
        var export = DoraAnalyzer.Load(config);
        var markdown = DoraRenderer.Markdown(config, export, report);
        Assert.Matches(FileLine, markdown);
        Assert.Contains("`dora/unevidenced-failure`", markdown);
        Assert.Contains("`dora/unshipped-change`", markdown);
        Assert.DoesNotContain("✅ Clean export", markdown);

        // errors render before warnings in the findings table (the joins table
        // lists rule names too, so the comparison happens inside the findings)
        var findingsAt = markdown.IndexOf("## Findings", StringComparison.Ordinal);
        var errorAt = markdown.IndexOf("`dora/unevidenced-failure`", findingsAt, StringComparison.Ordinal);
        var warningAt = markdown.IndexOf("`dora/unshipped-change`", findingsAt, StringComparison.Ordinal);
        Assert.True(errorAt < warningAt, "errors must render before warnings");
    }

    [Fact]
    public void Markdown_clean_board_documents_joins_and_definition_version()
    {
        var config = new DoraConfig { Root = _app.Root };
        var export = DoraAnalyzer.Load(config);
        var markdown = DoraRenderer.Markdown(config, export, _app.Extract());
        Assert.Contains("sfg dora 0.9.0", markdown);
        Assert.Contains("dora-definitions-v1", markdown);
        Assert.Contains("✅ Clean export", markdown);
        Assert.Contains("deployment → change", markdown);
        Assert.Contains("deployment → build", markdown);
        Assert.Contains("failure → restore evidence", markdown);
        Assert.Contains("Time series (week)", markdown);
        Assert.Contains("33.3%", markdown);
    }

    [Fact]
    public void Json_carries_the_series_the_joins_and_the_findings()
    {
        var config = new DoraConfig { Root = _app.Root };
        var export = DoraAnalyzer.Load(config);
        var json = DoraRenderer.Json(config, export, _app.Extract());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("sfg dora 0.9.0", root.GetProperty("generator").GetString());
        Assert.Equal("dora-definitions-v1", root.GetProperty("definitionVersion").GetString());
        Assert.Equal("week", root.GetProperty("period").GetString());
        Assert.Equal("example/api", root.GetProperty("repository").GetString());
        Assert.Equal(6, root.GetProperty("joins").GetArrayLength());
        Assert.Equal(2, root.GetProperty("series").GetArrayLength());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
        Assert.Equal(0, root.GetProperty("warnings").GetInt32());

        var week1 = root.GetProperty("series")[0];
        Assert.Equal("2026-06-01", week1.GetProperty("period").GetString());
        Assert.Equal(33.3, week1.GetProperty("changeFailureRate").GetDouble());
        Assert.Equal(2.5, week1.GetProperty("mttrHours").GetDouble());
    }

    [Fact]
    public void Definitions_file_is_versioned_and_deterministic()
    {
        var first = DoraRenderer.DefinitionsJson();
        var second = DoraRenderer.DefinitionsJson();
        Assert.Equal(first, second);

        using var document = System.Text.Json.JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal("dora-definitions-v1", root.GetProperty("version").GetString());
        Assert.Equal(4, root.GetProperty("keys").EnumerateObject().Count());
        Assert.Equal(6, root.GetProperty("joins").EnumerateObject().Count());
        Assert.Equal(3, root.GetProperty("taxonomy").EnumerateObject().Count());
        Assert.Equal("revert", root.GetProperty("taxonomy").EnumerateObject().First().Name);
        Assert.Contains("re-run mislabeled", root.GetProperty("re-run-rule").GetString());
        Assert.Contains("trends, not scores", root.GetProperty("trend-policy").GetString());
    }

    [Fact]
    public void Rendering_is_deterministic_end_to_end()
    {
        var config = new DoraConfig { Root = _app.Root };
        var export = DoraAnalyzer.Load(config);
        var first = (Markdown: DoraRenderer.Markdown(config, export, DoraAnalyzer.Analyze(config, export)),
                     Json: DoraRenderer.Json(config, export, DoraAnalyzer.Analyze(config, export)));
        var second = (Markdown: DoraRenderer.Markdown(config, export, DoraAnalyzer.Analyze(config, export)),
                      Json: DoraRenderer.Json(config, export, DoraAnalyzer.Analyze(config, export)));
        Assert.Equal(first.Markdown, second.Markdown);
        Assert.Equal(first.Json, second.Json);
    }
}