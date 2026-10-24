using System.Text;
using Sfg.Toolkit.DoraMetrics;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic recorded DORA export (#68): two ISO weeks of deployments with
/// known, hand-computable metrics — the control — plus helpers to seed the
/// failure modes (re-runs mislabeled as deployments, dangling joins, unbuilt
/// releases, unknown taxonomy, unevidenced restores) so the tests can prove
/// the extractor bites and never passes silently.
/// </summary>
public sealed class DoraAppFixture : IDisposable
{
    public DoraAppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-dora-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Write("dora-events.json", Compose(BaseDeployments, BaseChanges, BaseBuilds, BaseFailures));
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

    /// <summary>Extracts from the export the config names — the override path
    /// is absolute so it resolves from any working directory.</summary>
    public DoraReport Extract(string? eventsName = null, DoraPeriod period = DoraPeriod.Week)
    {
        var config = new DoraConfig
        {
            Root = Root,
            Period = period,
            EventsOverride = eventsName is null ? null : Path.Combine(Root, eventsName),
        };
        return DoraAnalyzer.Analyze(config, DoraAnalyzer.Load(config));
    }

    public string Compose(string deployments, string changes, string builds, string failures) => $$"""
        {
          "source": {
            "repository": "example/api",
            "api": "api.github.com",
            "window": "2026-06-01..2026-06-08",
            "exportedAt": "2026-10-24T00:00:00Z",
            "note": "synthetic test estate"
          },
          "deployments": [
        {{Indent(deployments)}}
          ],
          "changes": [
        {{Indent(changes)}}
          ],
          "builds": [
        {{Indent(builds)}}
          ],
          "failures": [
        {{Indent(failures)}}
          ]
        }
        """;

    private static string Indent(string rows) => string.Join(",\n", rows.Split('\n').Select(r => "    " + r.Trim()));

    // ── the control: 4 deployments over two ISO weeks, 4 changes, 7 runs
    //    (6 attempts after dedup), 1 hotfix incident on release day. Week
    //    2026-06-01: 3 deployments, 3 changes (lead times 1.0 / 1.5 / 26.0 →
    //    median 1.5), CFR 1/3 = 33.3%, MTTR 2.5 h. Week 2026-06-08: 1
    //    deployment, 1 change (lead 0.5), no failures.

    public const string BaseDeployments = """
        { "id": "r1", "environment": "prod", "deployedAt": "2026-06-01T10:00:00Z", "targetSha": "6666666666666666666666666666666666666666", "prs": [1] }
        { "id": "r2", "environment": "prod", "deployedAt": "2026-06-02T10:00:00Z", "targetSha": "2222222222222222222222222222222222222222", "prs": [2] }
        { "id": "r3", "environment": "prod", "deployedAt": "2026-06-01T13:30:00Z", "targetSha": "3333333333333333333333333333333333333333", "prs": [3] }
        { "id": "r4", "environment": "prod", "deployedAt": "2026-06-08T10:00:00Z", "targetSha": "7777777777777777777777777777777777777777", "prs": [4] }
        """;

    public const string BaseChanges = """
        { "pr": 1, "title": "Add orders API", "mergedAt": "2026-06-01T09:00:00Z", "mergeSha": "1111111111111111111111111111111111111111" }
        { "pr": 2, "title": "Add billing", "mergedAt": "2026-06-01T08:00:00Z", "mergeSha": "2222222222222222222222222222222222222222" }
        { "pr": 3, "title": "Fix orders crash", "mergedAt": "2026-06-01T12:00:00Z", "mergeSha": "3333333333333333333333333333333333333333" }
        { "pr": 4, "title": "Add audit export", "mergedAt": "2026-06-08T09:30:00Z", "mergeSha": "4444444444444444444444444444444444444444" }
        """;

    public const string BaseBuilds = """
        { "run": 101, "workflow": "ci", "headSha": "1111111111111111111111111111111111111111", "conclusion": "success", "completedAt": "2026-06-01T09:01:00Z" }
        { "run": 102, "workflow": "ci", "headSha": "1111111111111111111111111111111111111111", "conclusion": "failure", "completedAt": "2026-06-01T09:02:00Z" }
        { "run": 103, "workflow": "ci", "headSha": "6666666666666666666666666666666666666666", "conclusion": "success", "completedAt": "2026-06-01T10:05:00Z" }
        { "run": 104, "workflow": "ci", "headSha": "2222222222222222222222222222222222222222", "conclusion": "success", "completedAt": "2026-06-02T10:01:00Z" }
        { "run": 105, "workflow": "ci", "headSha": "3333333333333333333333333333333333333333", "conclusion": "success", "completedAt": "2026-06-03T10:05:00Z" }
        { "run": 106, "workflow": "ci", "headSha": "4444444444444444444444444444444444444444", "conclusion": "success", "completedAt": "2026-06-08T09:40:00Z" }
        { "run": 108, "workflow": "ci", "headSha": "7777777777777777777777777777777777777777", "conclusion": "success", "completedAt": "2026-06-08T09:50:00Z" }
        """;

    public const string BaseFailures = """
        { "id": "orders-crash", "mode": "hotfix", "deployment": "r1", "detectedAt": "2026-06-01T11:00:00Z", "restoredAt": "2026-06-01T13:30:00Z", "evidence": { "hotfixPr": 3 } }
        """;

    // ── seeded rows for the failure modes ──────────────────────────────────

    /// <summary>r1 re-deployed an hour later at the same target sha — the
    /// #68 done-when: a re-run mislabeled as a new deployment.</summary>
    public const string ReRunDeployment =
        """{ "id": "r1-rerun", "environment": "prod", "deployedAt": "2026-06-01T11:00:00Z", "targetSha": "6666666666666666666666666666666666666666", "prs": [1] }""";

    public const string Change5 =
        """{ "pr": 5, "title": "Add search", "mergedAt": "2026-06-08T09:00:00Z", "mergeSha": "5555555555555555555555555555555555555555" }""";

    public const string Build5 =
        """{ "run": 107, "workflow": "ci", "headSha": "5555555555555555555555555555555555555555", "conclusion": "success", "completedAt": "2026-06-08T09:05:00Z" }""";
}
