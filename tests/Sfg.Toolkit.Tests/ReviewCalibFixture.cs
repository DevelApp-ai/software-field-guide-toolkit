using System.Text;
using Xunit;
using Sfg.Toolkit.ReviewCalib;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic recorded review-events export (#77): 20 decisions across two
/// queues with hand-computable catch rates, approval telemetry, and judge
/// calibration — the control — plus helpers to seed the failure modes (a
/// seeded defect approved cleanly, a role below the catch-rate floor, a
/// rubber-stamping queue) so the tests can prove the harness bites and
/// never passes silently.
/// </summary>
public sealed class ReviewCalibFixture : IDisposable
{
    public ReviewCalibFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-reviewcalib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Write("review-events.json", Compose(BaseReviews));
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(Root, name), content, new UTF8Encoding(false));

    /// <summary>Extracts from the export the config names — the override
    /// path is absolute so it resolves from any working directory.</summary>
    public ReviewCalibReport Extract(string? eventsName = null, ReviewCalibConfig? config = null)
    {
        var effective = config ?? new ReviewCalibConfig { Root = Root };
        if (eventsName is not null)
        {
            effective = new ReviewCalibConfig
            {
                Root = effective.Root,
                EventsOverride = Path.Combine(Root, eventsName),
                RubberStampApprovalRate = effective.RubberStampApprovalRate,
                RubberStampMedianSeconds = effective.RubberStampMedianSeconds,
                RubberStampMinReviews = effective.RubberStampMinReviews,
                CatchRateMin = effective.CatchRateMin,
                StalenessDays = effective.StalenessDays,
                MaxFindings = effective.MaxFindings,
            };
        }

        var export = ReviewCalibAnalyzer.Load(effective);
        return ReviewCalibAnalyzer.Analyze(effective, export);
    }

    /// <summary>Swaps the one-line row carrying the id — the export format
    /// is one row per line, so a variant is a row swap.</summary>
    public string WithRow(string rows, string id, string newRow)
    {
        var lines = rows.Split('\n').ToList();
        var index = lines.FindIndex(line => line.Contains($"\"id\": \"{id}\"", StringComparison.Ordinal));
        Assert.True(index >= 0, $"row '{id}' not found in the export");
        lines[index] = newRow;
        return string.Join("\n", lines);
    }

    public string Compose(string rows) => $$"""
        {
          "source": {
            "repository": "example/checkout",
            "window": "2026-09-21..2026-10-18",
            "exportedAt": "2026-10-20T00:00:00Z",
            "today": "2026-10-20",
            "note": "synthetic test estate"
          },
          "reviews": [
        {{Indent(rows)}}
          ]
        }
        """;

    private static string Indent(string rows) => string.Join(",\n", rows.Split('\n').Select(r => "    " + r.Trim()));

    // ── the control: 20 decisions, two queues (checkout 14, infra 6), two
    //    roles (senior 14, junior 6), 8 seeded defects all caught, judge
    //    agreement 4/5 = 0.8 with the one disagreement on rv-005 (the judge
    //    would have approved what jonas escalated). Checkout approves 8 of
    //    14 (57.1%) at a median decision latency of 630.0s; infra approves
    //    4 of 6 (66.7%) at 360.0s.

    public const string BaseReviews = """
        { "id": "rv-001", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-05T09:00:00Z", "decidedAt": "2026-10-05T09:04:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-002", "queue": "checkout", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-05T11:00:00Z", "decidedAt": "2026-10-05T11:05:00Z", "decision": "edit", "editsBeforeDecision": 2, "judgeAgrees": true, "seeded": { "seedId": "seed-01", "expectedDecision": "edit" } }
        { "id": "rv-003", "queue": "checkout", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-10-06T09:30:00Z", "decidedAt": "2026-10-06T09:36:00Z", "decision": "reject", "editsBeforeDecision": 0, "judgeAgrees": true, "seeded": { "seedId": "seed-02", "expectedDecision": "reject" } }
        { "id": "rv-004", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-06T14:00:00Z", "decidedAt": "2026-10-06T14:07:00Z", "decision": "approve", "editsBeforeDecision": 1, "judgeAgrees": null, "seeded": null }
        { "id": "rv-005", "queue": "checkout", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-07T10:00:00Z", "decidedAt": "2026-10-07T10:08:00Z", "decision": "escalate", "editsBeforeDecision": 0, "judgeAgrees": false, "seeded": { "seedId": "seed-03", "expectedDecision": "reject" } }
        { "id": "rv-006", "queue": "checkout", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-10-07T15:00:00Z", "decidedAt": "2026-10-07T15:09:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-007", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-08T09:00:00Z", "decidedAt": "2026-10-08T09:10:00Z", "decision": "edit", "editsBeforeDecision": 3, "judgeAgrees": true, "seeded": { "seedId": "seed-04", "expectedDecision": "edit" } }
        { "id": "rv-008", "queue": "checkout", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-08T13:20:00Z", "decidedAt": "2026-10-08T13:31:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-009", "queue": "checkout", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-10-09T09:45:00Z", "decidedAt": "2026-10-09T09:57:00Z", "decision": "approve", "editsBeforeDecision": 1, "judgeAgrees": null, "seeded": null }
        { "id": "rv-010", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-10T10:00:00Z", "decidedAt": "2026-10-10T10:13:20Z", "decision": "reject", "editsBeforeDecision": 0, "judgeAgrees": true, "seeded": { "seedId": "seed-05", "expectedDecision": "reject" } }
        { "id": "rv-011", "queue": "checkout", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-14T08:00:00Z", "decidedAt": "2026-10-14T08:15:00Z", "decision": "edit", "editsBeforeDecision": 2, "judgeAgrees": null, "seeded": { "seedId": "seed-06", "expectedDecision": "edit" } }
        { "id": "rv-012", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-11T11:00:00Z", "decidedAt": "2026-10-11T11:17:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-013", "queue": "checkout", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-10-12T09:00:00Z", "decidedAt": "2026-10-12T09:20:00Z", "decision": "approve", "editsBeforeDecision": 1, "judgeAgrees": null, "seeded": null }
        { "id": "rv-014", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-13T16:00:00Z", "decidedAt": "2026-10-13T16:25:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-015", "queue": "infra", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-08T10:00:00Z", "decidedAt": "2026-10-08T10:03:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-016", "queue": "infra", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-10-09T14:00:00Z", "decidedAt": "2026-10-09T14:04:00Z", "decision": "edit", "editsBeforeDecision": 1, "judgeAgrees": null, "seeded": { "seedId": "seed-07", "expectedDecision": "edit" } }
        { "id": "rv-017", "queue": "infra", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-09T16:30:00Z", "decidedAt": "2026-10-09T16:35:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-018", "queue": "infra", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-12T10:00:00Z", "decidedAt": "2026-10-12T10:07:00Z", "decision": "reject", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-08", "expectedDecision": "reject" } }
        { "id": "rv-019", "queue": "infra", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-15T09:00:00Z", "decidedAt": "2026-10-15T09:09:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": null }
        { "id": "rv-020", "queue": "infra", "reviewerRole": "senior", "reviewer": "priya", "submittedAt": "2026-10-16T11:40:00Z", "decidedAt": "2026-10-16T11:52:00Z", "decision": "approve", "editsBeforeDecision": 1, "judgeAgrees": null, "seeded": null }
        """;

    /// <summary>rv-004 carrying seed-09, approved cleanly — the #77
    /// done-when: a seeded defect waved through.</summary>
    public const string MissedSeedRow = """{ "id": "rv-004", "queue": "checkout", "reviewerRole": "senior", "reviewer": "maya", "submittedAt": "2026-10-06T14:00:00Z", "decidedAt": "2026-10-06T14:07:00Z", "decision": "approve", "editsBeforeDecision": 1, "judgeAgrees": null, "seeded": { "seedId": "seed-09", "expectedDecision": "reject" } }""";

    /// <summary>rv-008 carrying seed-10, approved cleanly — junior drops
    /// to 3 of 4 (75.0%), below the 0.8 catch-rate floor.</summary>
    public const string LowCatchRow = """{ "id": "rv-008", "queue": "checkout", "reviewerRole": "junior", "reviewer": "jonas", "submittedAt": "2026-10-08T13:20:00Z", "decidedAt": "2026-10-08T13:31:00Z", "decision": "approve", "editsBeforeDecision": 0, "judgeAgrees": null, "seeded": { "seedId": "seed-10", "expectedDecision": "edit" } }""";
}