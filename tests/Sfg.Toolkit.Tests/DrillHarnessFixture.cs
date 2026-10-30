using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic resilience drill suite (#81): three scenarios with declared
/// clocks and expected telemetry signals, and a conformant recorded drill
/// export. Variants seed the failure modes (a late alert over its clock, an
/// over-RTO restore, a breached RPO, a missing alert or restore, missing
/// signals, unrecorded participants or versions, undeclared and duplicate
/// records, a never-drilled scenario, a stale one) so the tests can prove the
/// harness bites and never passes silently.
/// </summary>
public sealed class DrillHarnessFixture : IDisposable
{
    public DrillHarnessFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-drills-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "drills.json");

        Write("scenarios.json", """
            {
              "scenarios": [
                { "id": "r1", "title": "alert routing", "failure": "the paging route silently drops an alarm", "expectedSignals": [ "alarm/route-failed", "alarm/paged" ], "clocks": { "alertSeconds": 60, "rtoSeconds": 600, "rpoSeconds": 120 } },
                { "id": "r2", "title": "cache eviction restore", "failure": "the warm cache is evicted mid-incident", "expectedSignals": [ "cache/evicted", "cache/warmed" ], "clocks": { "alertSeconds": 120, "rtoSeconds": 900, "rpoSeconds": 300 } },
                { "id": "r3", "title": "config rollback", "failure": "a bad config deploy breaks checkout", "expectedSignals": [ "config/bad-deploy", "config/rolled-back" ], "clocks": { "alertSeconds": 300, "rtoSeconds": 1800, "rpoSeconds": 600 }, "maxStalenessDays": 30 }
              ]
            }
            """);
        Write("drill-evidence.json", Compose(BaseRecords));
        Write("drills.json", """
            {
              "name": "support-drills",
              "judge": "test-judge-v1",
              "stalenessDays": 90,
              "scenarios": "scenarios.json",
              "evidence": "drill-evidence.json"
            }
            """);
    }

    public string Root { get; }

    public string ConfigPath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    public string Compose(string rows) => $$"""
        {
          "drill": "test-drill-1",
          "asOf": "2026-10-15T00:00:00Z",
          "records": [
        {{string.Join(",\n", rows.Split('\n').Select(r => "    " + r.Trim()))}}
          ]
        }
        """;

    /// <summary>Swaps the one-line record carrying the scenario — the export
    /// format is one record per line, so a variant is a row swap.</summary>
    public string WithRecord(string rows, string scenario, string newRow)
    {
        var lines = rows.Split('\n').ToList();
        var index = lines.FindIndex(line => line.Contains($"\"scenario\": \"{scenario}\"", StringComparison.Ordinal));
        Assert.True(index >= 0, $"record '{scenario}' not found in the evidence");
        lines[index] = newRow;
        return string.Join("\n", lines);
    }

    /// <summary>Drops the one-line record carrying the scenario.</summary>
    public string WithoutRecord(string rows, string scenario)
    {
        var lines = rows.Split('\n').ToList();
        var index = lines.FindIndex(line => line.Contains($"\"scenario\": \"{scenario}\"", StringComparison.Ordinal));
        Assert.True(index >= 0, $"record '{scenario}' not found in the evidence");
        lines.RemoveAt(index);
        return string.Join("\n", lines);
    }

    /// <summary>Appends a record — the duplicate-drill variant.</summary>
    public string AppendRecord(string rows, string newRow) => rows.TrimEnd() + "\n" + newRow;

    // ── the control: every clock met with room to spare, every expected
    //    signal recorded, participants and versions named, every scenario
    //    drilled well inside its freshness window.

    public const string BaseRecords = """
        { "scenario": "r1", "startedAt": "2026-10-10T08:00:00Z", "alertedAt": "2026-10-10T08:00:40Z", "restoredAt": "2026-10-10T08:08:00Z", "lastRecoverableAt": "2026-10-10T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-10T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-10T08:00:40Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }
        { "scenario": "r2", "startedAt": "2026-10-12T09:00:00Z", "alertedAt": "2026-10-12T09:01:00Z", "restoredAt": "2026-10-12T09:12:00Z", "lastRecoverableAt": "2026-10-12T09:08:00Z", "signals": [ { "key": "cache/evicted", "at": "2026-10-12T09:00:40Z" }, { "key": "cache/warmed", "at": "2026-10-12T09:12:00Z" } ], "participants": [ "platform-on-call" ], "versions": { "cache": "5.3" } }
        { "scenario": "r3", "startedAt": "2026-10-14T10:00:00Z", "alertedAt": "2026-10-14T10:03:00Z", "restoredAt": "2026-10-14T10:20:00Z", "lastRecoverableAt": "2026-10-14T10:12:00Z", "signals": [ { "key": "config/bad-deploy", "at": "2026-10-14T10:00:10Z" }, { "key": "config/rolled-back", "at": "2026-10-14T10:20:00Z" } ], "participants": [ "deploy-on-call" ], "versions": { "config": "9.0" } }
        """;

    /// <summary>r1 alerts 120s after injection — over the 60s clock.</summary>
    public const string LateR1 =
        """{ "scenario": "r1", "startedAt": "2026-10-10T08:00:00Z", "alertedAt": "2026-10-10T08:02:00Z", "restoredAt": "2026-10-10T08:08:00Z", "lastRecoverableAt": "2026-10-10T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-10T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-10T08:02:00Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }""";

    /// <summary>r1 alerts exactly at the 60s clock — a budget met at the
    /// boundary is met, no finding.</summary>
    public const string AtClockR1 =
        """{ "scenario": "r1", "startedAt": "2026-10-10T08:00:00Z", "alertedAt": "2026-10-10T08:01:00Z", "restoredAt": "2026-10-10T08:08:00Z", "lastRecoverableAt": "2026-10-10T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-10T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-10T08:01:00Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }""";

    /// <summary>r2 restores 960s after injection — over the 900s RTO.</summary>
    public const string OverRtoR2 =
        """{ "scenario": "r2", "startedAt": "2026-10-12T09:00:00Z", "alertedAt": "2026-10-12T09:01:00Z", "restoredAt": "2026-10-12T09:16:00Z", "lastRecoverableAt": "2026-10-12T09:12:00Z", "signals": [ { "key": "cache/evicted", "at": "2026-10-12T09:00:40Z" }, { "key": "cache/warmed", "at": "2026-10-12T09:16:00Z" } ], "participants": [ "platform-on-call" ], "versions": { "cache": "5.3" } }""";

    /// <summary>r2 restores exactly at the 900s RTO — met at the boundary.</summary>
    public const string AtRtoR2 =
        """{ "scenario": "r2", "startedAt": "2026-10-12T09:00:00Z", "alertedAt": "2026-10-12T09:01:00Z", "restoredAt": "2026-10-12T09:15:00Z", "lastRecoverableAt": "2026-10-12T09:11:00Z", "signals": [ { "key": "cache/evicted", "at": "2026-10-12T09:00:40Z" }, { "key": "cache/warmed", "at": "2026-10-12T09:15:00Z" } ], "participants": [ "platform-on-call" ], "versions": { "cache": "5.3" } }""";

    /// <summary>r3 restores 660s after its last recoverable data — over the
    /// 600s RPO.</summary>
    public const string RpoR3 =
        """{ "scenario": "r3", "startedAt": "2026-10-14T10:00:00Z", "alertedAt": "2026-10-14T10:03:00Z", "restoredAt": "2026-10-14T10:20:00Z", "lastRecoverableAt": "2026-10-14T10:09:00Z", "signals": [ { "key": "config/bad-deploy", "at": "2026-10-14T10:00:10Z" }, { "key": "config/rolled-back", "at": "2026-10-14T10:20:00Z" } ], "participants": [ "deploy-on-call" ], "versions": { "config": "9.0" } }""";

    /// <summary>r3 restores exactly 600s after its last recoverable data —
    /// the RPO held at the boundary.</summary>
    public const string AtRpoR3 =
        """{ "scenario": "r3", "startedAt": "2026-10-14T10:00:00Z", "alertedAt": "2026-10-14T10:03:00Z", "restoredAt": "2026-10-14T10:20:00Z", "lastRecoverableAt": "2026-10-14T10:10:00Z", "signals": [ { "key": "config/bad-deploy", "at": "2026-10-14T10:00:10Z" }, { "key": "config/rolled-back", "at": "2026-10-14T10:20:00Z" } ], "participants": [ "deploy-on-call" ], "versions": { "config": "9.0" } }""";

    /// <summary>r1 never fires its alert.</summary>
    public const string NoAlertR1 =
        """{ "scenario": "r1", "startedAt": "2026-10-10T08:00:00Z", "restoredAt": "2026-10-10T08:08:00Z", "lastRecoverableAt": "2026-10-10T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-10T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-10T08:08:00Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }""";

    /// <summary>r2 never restores within the record.</summary>
    public const string NoRestoreR2 =
        """{ "scenario": "r2", "startedAt": "2026-10-12T09:00:00Z", "alertedAt": "2026-10-12T09:01:00Z", "signals": [ { "key": "cache/evicted", "at": "2026-10-12T09:00:40Z" }, { "key": "cache/warmed", "at": "2026-10-12T09:14:00Z" } ], "participants": [ "platform-on-call" ], "versions": { "cache": "5.3" } }""";

    /// <summary>r3 restores but records no last-recoverable data timestamp —
    /// the RPO is unverifiable: advisory warning.</summary>
    public const string NoLastRecoverableR3 =
        """{ "scenario": "r3", "startedAt": "2026-10-14T10:00:00Z", "alertedAt": "2026-10-14T10:03:00Z", "restoredAt": "2026-10-14T10:20:00Z", "signals": [ { "key": "config/bad-deploy", "at": "2026-10-14T10:00:10Z" }, { "key": "config/rolled-back", "at": "2026-10-14T10:20:00Z" } ], "participants": [ "deploy-on-call" ], "versions": { "config": "9.0" } }""";

    /// <summary>r2 records only one of its two expected signals.</summary>
    public const string SignalMissR2 =
        """{ "scenario": "r2", "startedAt": "2026-10-12T09:00:00Z", "alertedAt": "2026-10-12T09:01:00Z", "restoredAt": "2026-10-12T09:12:00Z", "lastRecoverableAt": "2026-10-12T09:08:00Z", "signals": [ { "key": "cache/warmed", "at": "2026-10-12T09:12:00Z" } ], "participants": [ "platform-on-call" ], "versions": { "cache": "5.3" } }""";

    /// <summary>r1 records a signal the catalog does not expect — advisory
    /// warning, the record disagrees with the catalog.</summary>
    public const string UnknownSignalR1 =
        """{ "scenario": "r1", "startedAt": "2026-10-10T08:00:00Z", "alertedAt": "2026-10-10T08:00:40Z", "restoredAt": "2026-10-10T08:08:00Z", "lastRecoverableAt": "2026-10-10T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-10T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-10T08:00:40Z" }, { "key": "alarm/escalated", "at": "2026-10-10T08:05:00Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }""";

    /// <summary>r1 names no participants.</summary>
    public const string NoParticipantsR1 =
        """{ "scenario": "r1", "startedAt": "2026-10-10T08:00:00Z", "alertedAt": "2026-10-10T08:00:40Z", "restoredAt": "2026-10-10T08:08:00Z", "lastRecoverableAt": "2026-10-10T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-10T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-10T08:00:40Z" } ], "versions": { "pager": "2.1" } }""";

    /// <summary>r2 names no component versions.</summary>
    public const string NoVersionsR2 =
        """{ "scenario": "r2", "startedAt": "2026-10-12T09:00:00Z", "alertedAt": "2026-10-12T09:01:00Z", "restoredAt": "2026-10-12T09:12:00Z", "lastRecoverableAt": "2026-10-12T09:08:00Z", "signals": [ { "key": "cache/evicted", "at": "2026-10-12T09:00:40Z" }, { "key": "cache/warmed", "at": "2026-10-12T09:12:00Z" } ], "participants": [ "platform-on-call" ] }""";

    /// <summary>A record targets a scenario the catalog never declared.</summary>
    public const string UndeclaredR9 =
        """{ "scenario": "r9", "startedAt": "2026-10-13T07:00:00Z", "alertedAt": "2026-10-13T07:00:30Z", "restoredAt": "2026-10-13T07:10:00Z", "lastRecoverableAt": "2026-10-13T07:08:00Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-13T07:00:20Z" }, { "key": "alarm/paged", "at": "2026-10-13T07:00:30Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }""";

    /// <summary>r1 is recorded twice in one export.</summary>
    public const string DuplicateR1 =
        """{ "scenario": "r1", "startedAt": "2026-10-11T08:00:00Z", "alertedAt": "2026-10-11T08:00:40Z", "restoredAt": "2026-10-11T08:08:00Z", "lastRecoverableAt": "2026-10-11T08:06:30Z", "signals": [ { "key": "alarm/route-failed", "at": "2026-10-11T08:00:30Z" }, { "key": "alarm/paged", "at": "2026-10-11T08:00:40Z" } ], "participants": [ "on-call" ], "versions": { "pager": "2.1" } }""";

    /// <summary>r3 was last drilled on 2026-06-01 — 136 days before asOf,
    /// over its own 30-day window (the scenario override, not the config's
    /// 90): advisory warning, the clocks themselves still met.</summary>
    public const string StaleR3 =
        """{ "scenario": "r3", "startedAt": "2026-06-01T10:00:00Z", "alertedAt": "2026-06-01T10:03:00Z", "restoredAt": "2026-06-01T10:20:00Z", "lastRecoverableAt": "2026-06-01T10:12:00Z", "signals": [ { "key": "config/bad-deploy", "at": "2026-06-01T10:00:10Z" }, { "key": "config/rolled-back", "at": "2026-06-01T10:20:00Z" } ], "participants": [ "deploy-on-call" ], "versions": { "config": "9.0" } }""";
}
