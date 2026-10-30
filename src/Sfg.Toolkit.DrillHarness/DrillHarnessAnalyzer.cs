namespace Sfg.Toolkit.DrillHarness;

/// <summary>
/// Runs the resilience drill evidence harness (#81): judges the recorded
/// drill export post-hoc against the committed scenario catalog — the alert
/// fired inside its escalation clock, the restore met the RTO, the RPO was
/// respected, the expected telemetry signals were recorded, participants and
/// versions are on the record, and every scenario is drilled fresh (a DR plan
/// that exists on paper and was never rehearsed is a finding). It judges
/// recorded evidence only — it never injects failures.
/// </summary>
public static class DrillAnalyzer
{
    public static DrillReport Analyze(
        DrillHarnessConfig config,
        IReadOnlyList<DrillScenario> scenarios,
        DrillEvidence evidence,
        string? evidencePathUsed = null)
    {
        var findings = new List<DrillFinding>();
        var scenarioNames = Path.GetFileName(evidencePathUsed ?? config.EvidencePath);
        var catalogName = Path.GetFileName(config.ScenariosPath);

        // Group records per scenario, in catalog-declared order; duplicates
        // and undeclared scenarios are loud errors.
        var byScenario = new Dictionary<string, List<DrillRecord>>(StringComparer.Ordinal);
        foreach (var record in evidence.Records)
        {
            if (!scenarios.Any(s => string.Equals(s.Id, record.Scenario, StringComparison.Ordinal)))
            {
                findings.Add(new DrillFinding(
                    "drill/evidence-undeclared",
                    "error",
                    $"drill record targets '{record.Scenario}' which is not in the catalog — the scenario population is declared by scenarios.json, never guessed",
                    new[] { record.Anchor(scenarioNames) }));
                continue;
            }

            if (byScenario.TryGetValue(record.Scenario, out var existing))
            {
                findings.Add(new DrillFinding(
                    "drill/duplicate-drill",
                    "error",
                    $"scenario '{record.Scenario}' is recorded twice in drill '{evidence.Drill}' — one drill, one record",
                    new[] { record.Anchor(scenarioNames), existing[0].Anchor(scenarioNames) }));
                existing.Add(record);
            }
            else
            {
                byScenario[record.Scenario] = new List<DrillRecord> { record };
            }
        }

        // Clocks and per-record coverage, then freshness, in catalog order.
        var clockRows = new List<ClockRow>();
        var signalRows = new List<SignalRow>();
        var participationRows = new List<ParticipationRow>();
        foreach (var scenario in scenarios.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var records = byScenario.TryGetValue(scenario.Id, out var list)
                ? list.ToArray()
                : Array.Empty<DrillRecord>();

            foreach (var record in records)
            {
                AnalyzeRecord(
                    scenario, record, catalogName, scenarioNames,
                    findings, clockRows, signalRows, participationRows);
            }

            if (records.Length == 0)
            {
                findings.Add(new DrillFinding(
                    "drill/never-drilled",
                    "error",
                    $"scenario '{scenario.Id}' is in the catalog but was never drilled — a DR plan on paper and a rehearsed one must not look alike",
                    new[] { scenario.Anchor(catalogName) }));
            }
        }

        var freshness = new List<FreshnessRow>();
        foreach (var scenario in scenarios.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var records = byScenario.TryGetValue(scenario.Id, out var list)
                ? list.ToArray()
                : Array.Empty<DrillRecord>();
            var window = scenario.MaxStalenessDays ?? config.StalenessDays;

            if (records.Length == 0)
            {
                freshness.Add(new FreshnessRow(
                    scenario.Id, scenario.Title, null, null, window, "never", scenario.Anchor(catalogName)));
                continue;
            }

            var latest = records.MaxBy(r => r.StartedAt)!;
            var latestAnchor = records
                .OrderBy(r => r.StartedAt)
                .Last()
                .Anchor(scenarioNames);
            var ageDays = (evidence.AsOf - latest.StartedAt).TotalDays;

            if (ageDays > window)
            {
                findings.Add(new DrillFinding(
                    "drill/stale-scenario",
                    "warning",
                    $"scenario '{scenario.Id}' was last drilled {Math.Round(ageDays, 0, MidpointRounding.AwayFromZero):0} days before {evidence.AsOf:yyyy-MM-dd} — over the {window}-day freshness window",
                    new[] { scenario.Anchor(catalogName), latestAnchor }));
            }

            freshness.Add(new FreshnessRow(
                scenario.Id,
                scenario.Title,
                latest.StartedAt,
                Math.Round(ageDays, 0, MidpointRounding.AwayFromZero),
                window,
                ageDays > window ? "stale" : "current",
                latestAnchor));
        }

        var ordered = findings
            .Distinct()
            .OrderBy(f => f, DrillFindingComparer.Instance)
            .ToArray();
        var rendered = ordered.Take(config.MaxFindings).ToArray();

        return new DrillReport
        {
            Suite = config.Name,
            Judge = config.Judge,
            Drill = evidence.Drill,
            AsOf = evidence.AsOf,
            StalenessDays = config.StalenessDays,
            Freshness = freshness.ToArray(),
            Clocks = clockRows
                .OrderBy(r => r.Scenario, StringComparer.Ordinal)
                .ThenBy(r => r.Anchor, StringComparer.Ordinal)
                .ToArray(),
            Signals = signalRows
                .OrderBy(r => r.Scenario, StringComparer.Ordinal)
                .ThenBy(r => r.Anchor, StringComparer.Ordinal)
                .ToArray(),
            Participation = participationRows
                .OrderBy(r => r.Scenario, StringComparer.Ordinal)
                .ThenBy(r => r.Anchor, StringComparer.Ordinal)
                .ToArray(),
            Findings = ordered,
            RenderedFindings = rendered,
            Errors = ordered.Count(f => f.Severity == "error"),
            Warnings = ordered.Count(f => f.Severity == "warning"),
            Cap = config.MaxFindings,
        };
    }

    private static void AnalyzeRecord(
        DrillScenario scenario,
        DrillRecord record,
        string catalogName,
        string scenarioNames,
        List<DrillFinding> findings,
        List<ClockRow> clockRows,
        List<SignalRow> signalRows,
        List<ParticipationRow> participationRows)
    {
        var breaches = new List<string>();

        // The alert clock: injection → alert.
        double? alertSeconds = null;
        if (record.AlertedAt is { } alerted)
        {
            alertSeconds = (alerted - record.StartedAt).TotalSeconds;
            if (alertSeconds.Value > scenario.AlertSeconds)
            {
                breaches.Add("late alert");
                findings.Add(new DrillFinding(
                    "drill/late-alert",
                    "error",
                    $"scenario '{scenario.Id}' alerted {Secs(alertSeconds.Value)}s after injection — over the {Secs(scenario.AlertSeconds)}s alert clock",
                    new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
            }
        }
        else
        {
            breaches.Add("alert never fired");
            findings.Add(new DrillFinding(
                "drill/alert-never-fired",
                "error",
                $"scenario '{scenario.Id}' never fired its alert within the drill record — the escalation clock cannot start",
                new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
        }

        // The restore clock (RTO): injection → restored.
        double? restoreSeconds = null;
        if (record.RestoredAt is { } restored)
        {
            restoreSeconds = (restored - record.StartedAt).TotalSeconds;
            if (restoreSeconds.Value > scenario.RtoSeconds)
            {
                breaches.Add("restore over RTO");
                findings.Add(new DrillFinding(
                    "drill/restore-over-rto",
                    "error",
                    $"scenario '{scenario.Id}' restored {Secs(restoreSeconds.Value)}s after injection — over the {Secs(scenario.RtoSeconds)}s RTO",
                    new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
            }
        }
        else
        {
            breaches.Add("never restored");
            findings.Add(new DrillFinding(
                "drill/restore-never-happened",
                "error",
                $"scenario '{scenario.Id}' never restored within the drill record — the RTO was not met",
                new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
        }

        // The data-loss window (RPO): last recoverable data → restored.
        double? rpoSeconds = null;
        if (record.RestoredAt is { } restoredAt && record.LastRecoverableAt is { } lastRecoverable)
        {
            rpoSeconds = (restoredAt - lastRecoverable).TotalSeconds;
            if (rpoSeconds.Value > scenario.RpoSeconds)
            {
                breaches.Add("RPO breached");
                findings.Add(new DrillFinding(
                    "drill/rpo-breached",
                    "error",
                    $"scenario '{scenario.Id}' restored {Secs(rpoSeconds.Value)}s after its last recoverable data — over the {Secs(scenario.RpoSeconds)}s RPO",
                    new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
            }
        }
        else if (record.RestoredAt is not null)
        {
            findings.Add(new DrillFinding(
                "drill/rpo-unrecorded",
                "warning",
                $"scenario '{scenario.Id}' restored but recorded no last-recoverable data timestamp — the RPO is unverifiable",
                new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
        }

        // Expected telemetry signals must be on the record; stray ones are advisory.
        var recorded = new HashSet<string>(record.Signals.Select(s => s.Key), StringComparer.Ordinal);
        var missing = scenario.ExpectedSignals
            .Where(key => !recorded.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        foreach (var key in missing)
        {
            findings.Add(new DrillFinding(
                "drill/signal-missing",
                "error",
                $"scenario '{scenario.Id}' never recorded the '{key}' signal the catalog expects a drill to capture",
                new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
        }

        var expected = new HashSet<string>(scenario.ExpectedSignals, StringComparer.Ordinal);
        foreach (var signal in record.Signals.Where(s => !expected.Contains(s.Key))
                     .OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            findings.Add(new DrillFinding(
                "drill/unknown-signal",
                "warning",
                $"scenario '{scenario.Id}' recorded the '{signal.Key}' signal which the catalog does not expect — extend the catalog or fix the record",
                new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
        }

        // Participants and versions must be on the record — evidence that
        // evaporates names no one.
        var participantsOk = record.Participants.Count > 0;
        var versionsOk = record.Versions.Count > 0;
        if (!participantsOk || !versionsOk)
        {
            findings.Add(new DrillFinding(
                "drill/participants-unrecorded",
                "error",
                !participantsOk && !versionsOk
                    ? $"scenario '{scenario.Id}' recorded neither participants nor component versions — drill evidence must name who ran it and what ran"
                    : !participantsOk
                        ? $"scenario '{scenario.Id}' recorded no participants — drill evidence must name who ran it"
                        : $"scenario '{scenario.Id}' recorded no component versions — drill evidence must name what ran",
                new[] { scenario.Anchor(catalogName), record.Anchor(scenarioNames) }));
        }

        clockRows.Add(new ClockRow(
            scenario.Id,
            alertSeconds is null ? null : Math.Round(alertSeconds.Value, 0, MidpointRounding.AwayFromZero),
            scenario.AlertSeconds,
            restoreSeconds is null ? null : Math.Round(restoreSeconds.Value, 0, MidpointRounding.AwayFromZero),
            scenario.RtoSeconds,
            rpoSeconds is null ? null : Math.Round(rpoSeconds.Value, 0, MidpointRounding.AwayFromZero),
            scenario.RpoSeconds,
            breaches.Count == 0 ? "clean" : string.Join(", ", breaches),
            record.Anchor(scenarioNames)));

        signalRows.Add(new SignalRow(
            scenario.Id,
            missing,
            record.Anchor(scenarioNames)));

        participationRows.Add(new ParticipationRow(
            scenario.Id,
            record.Participants.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            record.Versions
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")
                .ToArray(),
            participantsOk && versionsOk ? "recorded" : "unrecorded",
            record.Anchor(scenarioNames)));
    }

    private static long Secs(double seconds) => (long)Math.Round(seconds, 0, MidpointRounding.AwayFromZero);

    private sealed class DrillFindingComparer : IComparer<DrillFinding>
    {
        public static readonly DrillFindingComparer Instance = new();

        public int Compare(DrillFinding? x, DrillFinding? y) => (x ?? throw new ArgumentNullException(nameof(x)))
            .CompareTo(y ?? throw new ArgumentNullException(nameof(y)));
    }
}
