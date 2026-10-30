using System.Text.Json;
using Sfg.Toolkit.ReviewCalib;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// The reviewer calibration harness tests (#77): the control stays silent,
/// every calib/ rule bites with file:line evidence, the loud loader never
/// passes a malformed export silently, config thresholds are enforced, and
/// the boards are deterministic.
/// </summary>
public sealed class ReviewCalibTests : IClassFixture<ReviewCalibFixture>
{
    private readonly ReviewCalibFixture fixture;

    public ReviewCalibTests(ReviewCalibFixture fixture) => this.fixture = fixture;

    // ── the control: clean by construction ─────────────────────────────

    [Fact]
    public void Control_is_clean()
    {
        var report = fixture.Extract();
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Warnings);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Control_counts_the_estate()
    {
        var report = fixture.Extract();
        Assert.Equal(20, report.Reviews.Count);
        Assert.Equal(8, report.SeededCount);
        Assert.Equal(8, report.CaughtCount);
    }

    [Fact]
    public void Control_catch_rates_per_role()
    {
        var report = fixture.Extract();
        var senior = Assert.Single(report.Roles, r => r.Role == "senior");
        Assert.Equal(14, senior.Reviews);
        Assert.Equal(5, senior.Seeded);
        Assert.Equal(5, senior.Caught);
        var junior = Assert.Single(report.Roles, r => r.Role == "junior");
        Assert.Equal(3, junior.Seeded);
        Assert.Equal(3, junior.Caught);
        Assert.Equal(1.0, junior.CatchRate!.Value, 5);
    }

    [Fact]
    public void Control_queue_telemetry()
    {
        var report = fixture.Extract();
        var checkout = Assert.Single(report.Queues, q => q.Queue == "checkout");
        Assert.Equal(14, checkout.Reviews);
        Assert.Equal(8, checkout.Approves);
        Assert.Equal(3, checkout.Edits);
        Assert.Equal(2, checkout.Rejects);
        Assert.Equal(1, checkout.Escalations);
        Assert.Equal(0.5714, Math.Round(checkout.ApprovalRate, 4));
        Assert.Equal(630.0, checkout.MedianSeconds, 3);
        var infra = Assert.Single(report.Queues, q => q.Queue == "infra");
        Assert.Equal(0.6667, Math.Round(infra.ApprovalRate, 4));
        Assert.Equal(360.0, infra.MedianSeconds, 3);
    }

    [Fact]
    public void Control_judge_calibration()
    {
        var report = fixture.Extract();
        Assert.Equal(5, report.JudgeSample);
        Assert.Equal(0.8, report.Agreement!.Value, 5);
        var disagreement = Assert.Single(report.Disagreements);
        Assert.Equal("rv-005", disagreement);
    }

    [Fact]
    public void Control_rung_evidence()
    {
        var report = fixture.Extract();
        Assert.Equal(new[] { "checkout", "infra" }, report.Rungs.Select(r => r.Queue).ToArray());
        var checkout = Assert.Single(report.Rungs, r => r.Queue == "checkout");
        Assert.Equal(6, checkout.Seeded);
        Assert.Equal(1.0, checkout.CatchRate!.Value, 5);
        Assert.Equal(0.8, checkout.JudgeAgreement!.Value, 5);
        Assert.Equal("2026-10-14", checkout.LastSeededDecidedAt!.Value.ToString("yyyy-MM-dd"));
        var infra = Assert.Single(report.Rungs, r => r.Queue == "infra");
        Assert.Null(infra.JudgeAgreement);
        Assert.Equal("2026-10-12", infra.LastSeededDecidedAt!.Value.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void Control_board_renders_the_clean_line()
    {
        var markdown = BoardMarkdown();
        Assert.Contains("✅ Clean export: every seeded defect was caught, no queue rubber-stamps its approvals, and every role sits at or above the calibration floor.", markdown);
        Assert.Contains("sfg review-calib 0.10.0", markdown);
    }

    [Fact]
    public void Boards_are_deterministic()
    {
        var first = BoardMarkdown();
        var firstJson = BoardJson();
        Assert.Equal(first, BoardMarkdown());
        Assert.Equal(firstJson, BoardJson());
    }

    [Fact]
    public void Json_carries_the_generator_and_the_counts()
    {
        using var document = JsonDocument.Parse(BoardJson());
        var root = document.RootElement;
        Assert.Equal("sfg review-calib 0.10.0", root.GetProperty("generator").GetString());
        Assert.Equal(20, root.GetProperty("counts").GetProperty("reviews").GetInt32());
        Assert.Equal(8, root.GetProperty("counts").GetProperty("seeded").GetInt32());
        Assert.Equal(8, root.GetProperty("counts").GetProperty("caught").GetInt32());
        Assert.Equal(0, root.GetProperty("errors").GetInt32());
    }

    // ── the rules bite ─────────────────────────────────────────────────

    [Fact]
    public void Seed_missed_bites_with_cited_evidence()
    {
        fixture.Write("review-events.SeedMissed.json", fixture.Compose(fixture.WithRow(ReviewCalibFixture.BaseReviews, "rv-004", ReviewCalibFixture.MissedSeedRow)));
        var report = fixture.Extract("review-events.SeedMissed.json");
        Assert.Equal(1, report.Errors);
        var finding = Assert.Single(report.Findings, f => f.Rule == "calib/seed-missed");
        Assert.Equal("error", finding.Severity);
        Assert.Contains("seed-09", finding.Message);
        Assert.Contains("expected 'reject'", finding.Message);
        Assert.All(finding.Evidence, e => Assert.Matches(@":\d+$", e));
        Assert.Contains("review-events.SeedMissed.json", finding.Evidence[0]);
    }

    [Fact]
    public void Seed_missed_keeps_the_role_above_the_floor()
    {
        fixture.Write("review-events.SeedMissed.json", fixture.Compose(fixture.WithRow(ReviewCalibFixture.BaseReviews, "rv-004", ReviewCalibFixture.MissedSeedRow)));
        var report = fixture.Extract("review-events.SeedMissed.json");
        // senior: 5 of 6 = 83.3% — at or above the 0.8 floor, only the miss itself fires
        Assert.DoesNotContain(report.Findings, f => f.Rule == "calib/catch-rate-below");
        Assert.Single(report.Findings);
    }

    [Fact]
    public void Catch_rate_floor_bites_per_role()
    {
        fixture.Write("review-events.LowCatch.json", fixture.Compose(fixture.WithRow(ReviewCalibFixture.BaseReviews, "rv-008", ReviewCalibFixture.LowCatchRow)));
        var report = fixture.Extract("review-events.LowCatch.json");
        Assert.Equal(2, report.Errors);
        var floor = Assert.Single(report.Findings, f => f.Rule == "calib/catch-rate-below");
        Assert.Contains("'junior'", floor.Message);
        Assert.Contains("3 of 4", floor.Message);
        Assert.Contains("75.0%", floor.Message);
        Assert.Single(report.Findings, f => f.Rule == "calib/seed-missed");
        // errors first, then ordinal by rule
        Assert.Equal("calib/catch-rate-below", report.Findings[0].Rule);
    }

    [Fact]
    public void Catch_rate_floor_is_configurable()
    {
        fixture.Write("review-events.LowCatch.json", fixture.Compose(fixture.WithRow(ReviewCalibFixture.BaseReviews, "rv-008", ReviewCalibFixture.LowCatchRow)));
        // at the default 0.8 floor junior (0.75) is below; at 0.7 it is not
        var defaultReport = fixture.Extract("review-events.LowCatch.json");
        Assert.Contains(defaultReport.Findings, f => f.Rule == "calib/catch-rate-below");
        var relaxed = fixture.Extract("review-events.LowCatch.json", new ReviewCalibConfig { Root = fixture.Root, CatchRateMin = 0.7 });
        Assert.DoesNotContain(relaxed.Findings, f => f.Rule == "calib/catch-rate-below");
        // the miss itself always fires
        Assert.Contains(relaxed.Findings, f => f.Rule == "calib/seed-missed");
    }

    [Fact]
    public void Rubber_stamp_queue_bites()
    {
        fixture.Write("review-events.RubberStamp.json", RubberStampReviews());
        var report = fixture.Extract("review-events.RubberStamp.json");
        Assert.Equal(1, report.Errors);
        var rubber = Assert.Single(report.Findings, f => f.Rule == "calib/rubber-stamp-queue");
        Assert.Equal("error", rubber.Severity);
        Assert.Contains("12 of 12", rubber.Message);
        Assert.Contains("100.0%", rubber.Message);
        Assert.Contains("0.5s", rubber.Message);
        Assert.All(rubber.Evidence, e => Assert.Matches(@":\d+$", e));
        // a queue with no seeded decision is also stale — by construction
        Assert.Equal(1, report.Warnings);
        Assert.Contains(report.Findings, f => f.Rule == "calib/stale-queue");
    }

    [Fact]
    public void Small_fast_queue_warns_but_does_not_error()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 3).Select(i =>
            $$"""{ "id": "fast-00{{i}}", "queue": "fast", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-15T08:0{{i - 1}}:00Z", "decidedAt": "2026-10-15T08:0{{i - 1}}:00.{{i}}000000Z", "decision": "reject", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-f0{{i}}", "expectedDecision": "reject" } }"""));
        fixture.Write("review-events.FastSmall.json", fixture.Compose(rows));
        var report = fixture.Extract("review-events.FastSmall.json");
        // 3 reviews stay under the 10-review rubber threshold: warning, not error
        Assert.Equal(0, report.Errors);
        var subSecond = Assert.Single(report.Findings, f => f.Rule == "calib/sub-second-approval");
        Assert.Equal("warning", subSecond.Severity);
    }

    [Fact]
    public void Stale_queue_bites_when_the_window_is_cold()
    {
        var row = """{ "id": "old-001", "queue": "legacy", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-04-01T09:00:00Z", "decidedAt": "2026-04-01T09:10:00Z", "decision": "reject", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-old", "expectedDecision": "reject" } }""";
        fixture.Write("review-events.Stale.json", fixture.Compose(row));
        var report = fixture.Extract("review-events.Stale.json");
        var stale = Assert.Single(report.Findings, f => f.Rule == "calib/stale-queue");
        Assert.Equal("warning", stale.Severity);
        Assert.Contains("legacy", stale.Message);
        Assert.Contains("2026-10-20", stale.Message);
    }

    [Fact]
    public void Max_findings_caps_the_rendered_board()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 5).Select(i =>
            $$"""{ "id": "miss-00{{i}}", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-m0{{i}}", "expectedDecision": "reject" } }"""));
        fixture.Write("review-events.Misses.json", fixture.Compose(rows));
        var report = fixture.Extract("review-events.Misses.json", new ReviewCalibConfig { Root = fixture.Root, MaxFindings = 2 });
        Assert.Equal(6, report.Findings.Count); // 5 misses + the below-floor role
        Assert.Equal(2, report.RenderedFindings.Count);
        Assert.Equal(2, report.Cap);
        var markdown = BoardMarkdown("review-events.Misses.json", new ReviewCalibConfig { Root = fixture.Root, MaxFindings = 2 });
        Assert.Contains("…and 4 more (raise `maxFindings` beyond 2 to see all).", markdown);
    }

    [Fact]
    public void Events_override_is_verbatim()
    {
        // the override path resolves verbatim — a variant file at any path
        var report = fixture.Extract(eventsName: null, config: new ReviewCalibConfig
        {
            Root = fixture.Root,
            EventsOverride = Path.Combine(fixture.Root, "review-events.json"),
        });
        Assert.Equal(0, report.Errors);
    }

    // ── the loud loader never passes silently ──────────────────────────

    [Fact]
    public void Loader_throws_when_the_export_is_missing()
    {
        var config = new ReviewCalibConfig { Root = fixture.Root, EventsPath = "no-such-export.json" };
        Assert.Throws<FileNotFoundException>(() => ReviewCalibAnalyzer.Load(config));
    }

    [Fact]
    public void Loader_throws_on_malformed_json()
    {
        fixture.Write("review-events.Malformed.json", "{ not json");
        Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.Malformed.json"));
    }

    [Fact]
    public void Loader_throws_on_missing_reviews_array()
    {
        fixture.Write("review-events.NoReviews.json", "{ \"source\": { \"today\": \"2026-10-20\" } }");
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.NoReviews.json"));
        Assert.Contains("reviews", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_missing_today_anchor()
    {
        fixture.Write("review-events.NoToday.json", "{ \"source\": { \"repository\": \"example/checkout\" }, \"reviews\": [] }");
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.NoToday.json"));
        Assert.Contains("today", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_invalid_today_anchor()
    {
        fixture.Write("review-events.BadToday.json", "{ \"source\": { \"today\": \"October 20\" }, \"reviews\": [] }");
        Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.BadToday.json"));
    }

    [Fact]
    public void Loader_throws_on_duplicate_ids()
    {
        var row = """{ "id": "dup-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }""";
        fixture.Write("review-events.Duplicate.json", fixture.Compose(row + "\n" + row));
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.Duplicate.json"));
        Assert.Contains("duplicate review id 'dup-001'", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_unknown_decision()
    {
        var row = """{ "id": "weird-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "ship-it", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }""";
        fixture.Write("review-events.BadDecision.json", fixture.Compose(row));
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.BadDecision.json"));
        Assert.Contains("ship-it", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_decision_before_submission()
    {
        var row = """{ "id": "early-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:10:00Z", "decidedAt": "2026-10-05T09:00:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }""";
        fixture.Write("review-events.Early.json", fixture.Compose(row));
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.Early.json"));
        Assert.Contains("decided before it was submitted", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_invalid_timestamp()
    {
        var row = """{ "id": "time-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "yesterday", "decidedAt": "2026-10-05T09:00:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }""";
        fixture.Write("review-events.BadTime.json", fixture.Compose(row));
        Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.BadTime.json"));
    }

    [Fact]
    public void Loader_throws_on_missing_edits_counter()
    {
        var row = """{ "id": "noedit-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "approve", "judgeAgrees": null, "seeded": null }""";
        fixture.Write("review-events.NoEdits.json", fixture.Compose(row));
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.NoEdits.json"));
        Assert.Contains("editsBeforeDecision", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_seeded_without_expected_decision()
    {
        var row = """{ "id": "bare-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-bare" } }""";
        fixture.Write("review-events.BareSeed.json", fixture.Compose(row));
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.BareSeed.json"));
        Assert.Contains("expectedDecision", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_seeded_expected_approve()
    {
        var row = """{ "id": "approve-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-ok", "expectedDecision": "approve" } }""";
        fixture.Write("review-events.SeedApprove.json", fixture.Compose(row));
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.SeedApprove.json"));
        Assert.Contains("never approved", exception.Message);
    }

    [Fact]
    public void Loader_throws_on_non_boolean_judge()
    {
        var row = """{ "id": "judge-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:05:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": "probably", "seeded": null }""";
        fixture.Write("review-events.BadJudge.json", fixture.Compose(row));
        Assert.Throws<InvalidOperationException>(() => fixture.Extract("review-events.BadJudge.json"));
    }

    // ── config is loud too ──────────────────────────────────────────────

    [Fact]
    public void Config_loads_thresholds()
    {
        fixture.Write("review-calib.json",
            """
            {
              "events": "review-events.json",
              "rubberStampApprovalRate": 0.99,
              "rubberStampMedianSeconds": 2.0,
              "rubberStampMinReviews": 5,
              "catchRateMin": 0.9,
              "stalenessDays": 14,
              "maxFindings": 10
            }
            """);
        var config = ReviewCalibConfig.Load(Path.Combine(fixture.Root, "review-calib.json"), fixture.Root, null);
        Assert.Equal(0.99, config.RubberStampApprovalRate, 5);
        Assert.Equal(2.0, config.RubberStampMedianSeconds, 5);
        Assert.Equal(5, config.RubberStampMinReviews);
        Assert.Equal(0.9, config.CatchRateMin, 5);
        Assert.Equal(14, config.StalenessDays);
        Assert.Equal(10, config.MaxFindings);
        Assert.Equal(Path.Combine(fixture.Root, "review-events.json"), config.EventsFullPath);
    }

    [Fact]
    public void Config_throws_when_missing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ReviewCalibConfig.Load(Path.Combine(fixture.Root, "no-such-config.json"), fixture.Root, null));
    }

    [Fact]
    public void Config_throws_on_malformed_json()
    {
        fixture.Write("review-calib.json", "{ not json");
        Assert.Throws<InvalidOperationException>(() =>
            ReviewCalibConfig.Load(Path.Combine(fixture.Root, "review-calib.json"), fixture.Root, null));
    }

    [Theory]
    [InlineData("catchRateMin", "1.5", "fraction above 0 and at most 1")]
    [InlineData("catchRateMin", "0", "fraction above 0 and at most 1")]
    [InlineData("stalenessDays", "0", "positive integer")]
    [InlineData("maxFindings", "0", "positive integer")]
    [InlineData("rubberStampMedianSeconds", "0", "positive number of seconds")]
    [InlineData("rubberStampMinReviews", "1", "at least 2")]
    public void Config_throws_on_invalid_thresholds(string name, string value, string expected)
    {
        fixture.Write("review-calib.json", $$"""{ "{{name}}": {{value}} }""");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReviewCalibConfig.Load(Path.Combine(fixture.Root, "review-calib.json"), fixture.Root, null));
        Assert.Contains(name, exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Config_throws_on_empty_events()
    {
        fixture.Write("review-calib.json", "{ \"events\": \"\" }");
        Assert.Throws<InvalidOperationException>(() =>
            ReviewCalibConfig.Load(Path.Combine(fixture.Root, "review-calib.json"), fixture.Root, null));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private string BoardMarkdown(string? eventsName = null, ReviewCalibConfig? config = null)
    {
        var effective = config ?? new ReviewCalibConfig { Root = fixture.Root };
        if (eventsName is not null)
        {
            effective = new ReviewCalibConfig
            {
                Root = effective.Root,
                EventsOverride = Path.Combine(fixture.Root, eventsName),
                CatchRateMin = effective.CatchRateMin,
                MaxFindings = effective.MaxFindings,
            };
        }

        var export = ReviewCalibAnalyzer.Load(effective);
        var report = ReviewCalibAnalyzer.Analyze(effective, export);
        return ReviewCalibRenderer.Markdown(effective, export, report);
    }

    private string BoardJson()
    {
        var config = new ReviewCalibConfig { Root = fixture.Root };
        var export = ReviewCalibAnalyzer.Load(config);
        var report = ReviewCalibAnalyzer.Analyze(config, export);
        return ReviewCalibRenderer.Json(config, export, report);
    }

    private string RubberStampReviews()
    {
        var rows = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            var reviewer = i % 3 == 0 ? "priya" : i % 3 == 1 ? "maya" : "jonas";
            var role = reviewer == "jonas" ? "junior" : "senior";
            var seconds = 0.2 + i * 0.05;
            rows.Add($$"""{ "id": "rubber-{{i:D3}}", "queue": "checkout", "reviewerRole": "{{role}}", "reviewer": "{{reviewer}}", "submittedAt": "2026-10-15T08:{{(i - 1):D2}}:00Z", "decidedAt": "2026-10-15T08:{{(i - 1):D2}}:00.{{seconds * 10000000:0}}Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }""");
        }

        return fixture.Compose(string.Join("\n", rows));
    }
}
