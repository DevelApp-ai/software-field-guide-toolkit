using System.Globalization;
using System.Text.Json;

namespace Sfg.Toolkit.DoraMetrics;

/// <summary>The parsed, validated events export. Loud loader: a malformed
/// export never passes silently — the format is the contract (#68).</summary>
public sealed class DoraEventsExport
{
    public required string Path { get; init; }

    public required string FileName { get; init; }

    /// <summary>The raw export text — findings cite line numbers into it.</summary>
    public required string Raw { get; init; }

    public required IReadOnlyList<DoraDeployment> Deployments { get; init; }

    public required IReadOnlyList<DoraChange> Changes { get; init; }

    public required IReadOnlyList<DoraBuild> Builds { get; init; }

    public required IReadOnlyList<DoraFailure> Failures { get; init; }

    public string? Repository { get; init; }

    public string? Window { get; init; }

    /// <summary>1-based line of the first line containing the needle — the
    /// anchor every finding cites. A missing needle anchors on line 1 so the
    /// file:line contract holds even for absent rows.</summary>
    public string Anchor(string needle)
    {
        var line = 1;
        using var reader = new StringReader(Raw);
        while (reader.ReadLine() is { } current)
        {
            if (current.Contains(needle, StringComparison.Ordinal))
            {
                return $"{FileName}:{line}";
            }

            line++;
        }

        return $"{FileName}:1";
    }
}

/// <summary>
/// The DORA four-key extractor (#68). Reads a committed, recorded export
/// (never an API live), verifies every join the four keys depend on, and
/// trends the keys per period — deployment frequency, lead time for changes,
/// change failure rate, mean time to restore — against a versioned
/// definitions file. Trends, not scores.
/// </summary>
public static class DoraAnalyzer
{
    public static DoraEventsExport Load(DoraConfig config)
    {
        var path = config.EventsFullPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"dora events export not found: {path} — the extractor reads a recorded export (produce one in the documented format; it never calls an API live)");
        }

        var raw = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"dora events export is not valid JSON: {path}: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"dora events export must be a JSON object: {path}");
            }

            string? repository = null;
            string? window = null;
            if (root.TryGetProperty("source", out var source))
            {
                if (source.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("\"source\" must be a JSON object (repository, api, window, exportedAt, note)");
                }

                repository = ReadOptionalString(source, "repository");
                window = ReadOptionalString(source, "window");
            }

            var deployments = ReadArray(root, "deployments", path);
            var changes = ReadArray(root, "changes", path);
            var builds = ReadArray(root, "builds", path);
            var failures = ReadArray(root, "failures", path);

            var deploymentList = new List<DoraDeployment>();
            foreach (var entry in deployments.EnumerateArray())
            {
                var id = RequireString(entry, "id", "a deployment");
                deploymentList.Add(new DoraDeployment
                {
                    Id = id,
                    Environment = RequireString(entry, "environment", $"deployment '{id}'"),
                    DeployedAt = ReadTimestamp(entry, "deployedAt", $"deployment '{id}'"),
                    TargetSha = RequireString(entry, "targetSha", $"deployment '{id}'"),
                    Prs = ReadInts(entry, "prs", $"deployment '{id}'"),
                    Line = 0,
                });
            }

            foreach (var group in deploymentList.GroupBy(d => d.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate deployment id '{group.Key}' — one row per deployment, ambiguous is never allowed");
                }
            }

            var changeList = new List<DoraChange>();
            foreach (var entry in changes.EnumerateArray())
            {
                if (!entry.TryGetProperty("pr", out var prElement) ||
                    prElement.ValueKind != JsonValueKind.Number || !prElement.TryGetInt32(out var pr))
                {
                    throw new InvalidOperationException("every change needs an integer \"pr\" — the PR number it records");
                }

                changeList.Add(new DoraChange
                {
                    Pr = pr,
                    Title = RequireString(entry, "title", $"change #{pr}"),
                    MergedAt = ReadTimestamp(entry, "mergedAt", $"change #{pr}"),
                    MergeSha = RequireString(entry, "mergeSha", $"change #{pr}"),
                    Line = 0,
                });
            }

            foreach (var group in changeList.GroupBy(c => c.Pr))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate change #{group.Key} — one row per PR, ambiguous is never allowed");
                }
            }

            // build attempts de-duplicate by (workflow, headSha): one attempt
            // per workflow per sha — a re-queued run is the same attempt
            var buildList = new List<DoraBuild>();
            var seenAttempts = new HashSet<(string Workflow, string Sha)>();
            foreach (var entry in builds.EnumerateArray())
            {
                if (!entry.TryGetProperty("run", out var runElement) ||
                    runElement.ValueKind != JsonValueKind.Number || !runElement.TryGetInt32(out var run))
                {
                    throw new InvalidOperationException("every build needs an integer \"run\" — the CI run number it records");
                }

                var build = new DoraBuild
                {
                    Run = run,
                    Workflow = RequireString(entry, "workflow", $"build run {run}"),
                    HeadSha = RequireString(entry, "headSha", $"build run {run}"),
                    Conclusion = RequireString(entry, "conclusion", $"build run {run}"),
                    CompletedAt = ReadTimestamp(entry, "completedAt", $"build run {run}"),
                };

                if (seenAttempts.Add((build.Workflow, build.HeadSha)))
                {
                    buildList.Add(build);
                }
            }

            var failureList = new List<DoraFailure>();
            foreach (var entry in failures.EnumerateArray())
            {
                var id = RequireString(entry, "id", "a failure");
                var mode = RequireString(entry, "mode", $"failure '{id}'");
                var evidence = new DoraFailureEvidence();
                if (entry.TryGetProperty("evidence", out var evidenceElement))
                {
                    if (evidenceElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException($"failure '{id}' has an invalid \"evidence\" — it must be a JSON object");
                    }

                    evidence = new DoraFailureEvidence
                    {
                        RevertPr = ReadOptionalInt(evidenceElement, "revertPr", id),
                        HotfixPr = ReadOptionalInt(evidenceElement, "hotfixPr", id),
                        RollbackTo = ReadOptionalString(evidenceElement, "rollbackTo"),
                    };
                }

                failureList.Add(new DoraFailure
                {
                    Id = id,
                    Mode = mode,
                    Deployment = RequireString(entry, "deployment", $"failure '{id}'"),
                    DetectedAt = ReadTimestamp(entry, "detectedAt", $"failure '{id}'"),
                    RestoredAt = ReadTimestamp(entry, "restoredAt", $"failure '{id}'"),
                    Evidence = evidence,
                    Line = 0,
                });
            }

            foreach (var group in failureList.GroupBy(f => f.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate failure id '{group.Key}' — one row per incident, ambiguous is never allowed");
                }
            }

            return new DoraEventsExport
            {
                Path = path,
                FileName = System.IO.Path.GetFileName(path),
                Raw = raw,
                // machine order: deterministic everywhere
                Deployments = deploymentList
                    .OrderBy(d => d.DeployedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray(),
                Changes = changeList.OrderBy(c => c.Pr).ToArray(),
                Builds = buildList
                    .OrderBy(b => b.CompletedAt).ThenBy(b => b.Workflow, StringComparer.Ordinal).ThenBy(b => b.Run).ToArray(),
                Failures = failureList
                    .OrderBy(f => f.DetectedAt).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray(),
                Repository = repository,
                Window = window,
            };
        }
    }

    public static DoraReport Analyze(DoraConfig config, DoraEventsExport export)
    {
        var findings = new List<DoraFinding>();
        var changesByPr = export.Changes.ToDictionary(c => c.Pr);
        var deploymentsById = export.Deployments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var attemptsBySha = export.Builds
            .GroupBy(b => b.HeadSha, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        string AnchorDeployment(DoraDeployment deployment) => export.Anchor($"\"id\": \"{deployment.Id}\"");
        string AnchorChange(DoraChange change) => export.Anchor($"\"pr\": {change.Pr},");
        string AnchorFailure(DoraFailure failure) => export.Anchor($"\"id\": \"{failure.Id}\"");
        string Sha(string sha) => sha.Length > 8 ? sha[..8] : sha;

        // ── re-run de-duplication: same environment + same target sha is the
        // same deployment twice; the later row is a re-run mislabeled as a new
        // deployment — flagged, and excluded from every count (#68 done-when)
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in export.Deployments
                     .GroupBy(d => (d.Environment, d.TargetSha))
                     .OrderBy(g => g.First().DeployedAt).ThenBy(g => g.First().Id, StringComparer.Ordinal))
        {
            var rows = group.OrderBy(d => d.DeployedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
            for (var i = 1; i < rows.Length; i++)
            {
                excluded.Add(rows[i].Id);
                findings.Add(new DoraFinding(
                    "dora/re-run-mislabeled", "error",
                    $"deployment '{rows[i].Id}' re-runs environment '{rows[i].Environment}' at target sha {Sha(rows[i].TargetSha)} already deployed by '{rows[0].Id}' — a re-run is not a new deployment; it is excluded from the counts",
                    [AnchorDeployment(rows[0]), AnchorDeployment(rows[i])]));
            }
        }

        var active = export.Deployments.Where(d => !excluded.Contains(d.Id)).ToArray();

        // ── join: deployment → change (the PRs it says it ships)
        var prRefsVerified = 0;
        var prRefsTotal = 0;
        foreach (var deployment in export.Deployments)
        {
            foreach (var pr in deployment.Prs)
            {
                prRefsTotal++;
                if (!changesByPr.TryGetValue(pr, out var change))
                {
                    findings.Add(new DoraFinding(
                        "dora/dangling-join", "error",
                        $"deployment '{deployment.Id}' ships PR #{pr} which is not in the changes — the join dangles",
                        [AnchorDeployment(deployment)]));
                    continue;
                }

                if (change.MergedAt > deployment.DeployedAt)
                {
                    findings.Add(new DoraFinding(
                        "dora/temporal-join-violation", "error",
                        $"PR #{pr} merged {change.MergedAt:yyyy-MM-dd HH:mm:ss}Z after deployment '{deployment.Id}' shipped it at {deployment.DeployedAt:yyyy-MM-dd HH:mm:ss}Z — a cause cannot land after its effect",
                        [AnchorChange(change), AnchorDeployment(deployment)]));
                    continue;
                }

                prRefsVerified++;
            }
        }

        // ── join: deployment → build (the target sha was observed green)
        var builtDeployments = 0;
        foreach (var deployment in export.Deployments)
        {
            var green = attemptsBySha.TryGetValue(deployment.TargetSha, out var attempts) &&
                        attempts.Any(a => a.Conclusion == "success");
            if (!green)
            {
                findings.Add(new DoraFinding(
                    "dora/unbuilt-deployment", "error",
                    $"deployment '{deployment.Id}' shipped target sha {Sha(deployment.TargetSha)} that no successful CI run observes — the release is unbuilt",
                    [AnchorDeployment(deployment)]));
            }
            else
            {
                builtDeployments++;
            }
        }

        // ── join: change → build (the merge sha was observed by any run)
        var builtChanges = 0;
        foreach (var change in export.Changes)
        {
            var observed = attemptsBySha.TryGetValue(change.MergeSha, out var attempts) && attempts.Length > 0;
            if (!observed)
            {
                findings.Add(new DoraFinding(
                    "dora/change-without-build", "warning",
                    $"PR #{change.Pr} merged at {Sha(change.MergeSha)} that no CI run observes — the change is unverified by the pipeline",
                    [AnchorChange(change)]));
            }
            else
            {
                builtChanges++;
            }
        }

        // ── join: change → deployment (merged changes ship somewhere)
        var shippedChanges = 0;
        var shipsByPr = new Dictionary<int, List<DoraDeployment>>();
        foreach (var deployment in active)
        {
            foreach (var pr in deployment.Prs)
            {
                if (!shipsByPr.TryGetValue(pr, out var list))
                {
                    shipsByPr[pr] = list = [];
                }

                list.Add(deployment);
            }
        }

        foreach (var change in export.Changes)
        {
            if (!shipsByPr.ContainsKey(change.Pr))
            {
                findings.Add(new DoraFinding(
                    "dora/unshipped-change", "warning",
                    $"PR #{change.Pr} merged but no deployment ships it — the change never reached an environment in this export",
                    [AnchorChange(change)]));
            }
            else
            {
                shippedChanges++;
            }
        }

        // ── failures: the restore taxonomy is checked, never trusted
        var verifiedFailures = new List<DoraFailure>();
        foreach (var failure in export.Failures)
        {
            var joinsDeployment = deploymentsById.TryGetValue(failure.Deployment, out var deployment);
            if (!joinsDeployment)
            {
                findings.Add(new DoraFinding(
                    "dora/dangling-failure-join", "error",
                    $"failure '{failure.Id}' names deployment '{failure.Deployment}' which does not exist — the join dangles",
                    [AnchorFailure(failure)]));
            }

            if (failure.RestoredAt < failure.DetectedAt)
            {
                findings.Add(new DoraFinding(
                    "dora/restore-before-detection", "error",
                    $"failure '{failure.Id}' was restored {failure.RestoredAt:yyyy-MM-dd HH:mm:ss}Z before it was detected {failure.DetectedAt:yyyy-MM-dd HH:mm:ss}Z — service cannot return before it left",
                    [AnchorFailure(failure)]));
            }

            var knownMode = failure.Mode is "revert" or "rollback" or "hotfix";
            if (!knownMode)
            {
                findings.Add(new DoraFinding(
                    "dora/unknown-failure-mode", "error",
                    $"failure '{failure.Id}' records mode '{failure.Mode}' which is not in the taxonomy (revert, rollback, hotfix) — an unclassifiable restore is not a restore yet",
                    [AnchorFailure(failure)]));
            }

            var evidenced = knownMode && EvidenceChecks(failure, deployment, changesByPr, deploymentsById, findings, AnchorFailure);
            if (joinsDeployment && failure.RestoredAt >= failure.DetectedAt && knownMode && evidenced)
            {
                verifiedFailures.Add(failure);
            }
        }

        // ── the joins table: every join the four keys depend on, verified
        var joins = new List<DoraJoinStat>
        {
            new("deployment → change",
                "each deployment's PR references resolve to a change that merged before it shipped",
                prRefsVerified, prRefsTotal,
                "dora/dangling-join, dora/temporal-join-violation"),
            new("deployment → build",
                "each deployment's target sha was observed by a successful CI run",
                builtDeployments, export.Deployments.Count,
                "dora/unbuilt-deployment"),
            new("change → build",
                "each merged change's merge sha was observed by a CI run",
                builtChanges, export.Changes.Count,
                "dora/change-without-build"),
            new("change → deployment",
                "each merged change is shipped by some deployment",
                shippedChanges, export.Changes.Count,
                "dora/unshipped-change"),
            new("failure → deployment",
                "each failure names a deployment that exists",
                export.Failures.Count(f => deploymentsById.ContainsKey(f.Deployment)), export.Failures.Count,
                "dora/dangling-failure-join"),
            new("failure → restore evidence",
                "each failure's mode is in the taxonomy and its evidence checks out",
                verifiedFailures.Count, export.Failures.Count,
                "dora/unknown-failure-mode, dora/unevidenced-failure, dora/restore-before-detection"),
        };

        // ── the four-key series per period
        var series = BuildSeries(config, active, shipsByPr, changesByPr, verifiedFailures);

        findings.Sort(DoraFinding.Comparer);
        return new DoraReport
        {
            Deployments = export.Deployments,
            Changes = export.Changes,
            Builds = export.Builds,
            Failures = export.Failures,
            ExcludedDeployments = export.Deployments
                .Where(d => excluded.Contains(d.Id))
                .Select(d => d.Id)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Joins = joins,
            Series = series,
            Findings = findings,
            Cap = config.MaxFindings,
            Repository = export.Repository,
            Window = export.Window,
        };
    }

    private static bool EvidenceChecks(
        DoraFailure failure,
        DoraDeployment? deployment,
        Dictionary<int, DoraChange> changesByPr,
        Dictionary<string, DoraDeployment> deploymentsById,
        List<DoraFinding> findings,
        Func<DoraFailure, string> anchorFailure)
    {
        switch (failure.Mode)
        {
            case "revert":
            {
                var pr = failure.Evidence.RevertPr;
                if (pr is null || !changesByPr.TryGetValue(pr.Value, out var revert) ||
                    revert.MergedAt < failure.DetectedAt || revert.MergedAt > failure.RestoredAt)
                {
                    findings.Add(new DoraFinding(
                        "dora/unevidenced-failure", "error",
                        $"failure '{failure.Id}' records mode 'revert' but its revertPr " +
                        (pr is null ? "is missing" : $"#{pr} does not resolve to a change merged inside the incident window") +
                        " — an unevidenced restore is a hope, not a taxonomy entry",
                        [anchorFailure(failure)]));
                    return false;
                }

                return true;
            }

            case "hotfix":
            {
                var pr = failure.Evidence.HotfixPr;
                if (pr is null || !changesByPr.TryGetValue(pr.Value, out var hotfix) ||
                    hotfix.MergedAt < failure.DetectedAt || hotfix.MergedAt > failure.RestoredAt)
                {
                    findings.Add(new DoraFinding(
                        "dora/unevidenced-failure", "error",
                        $"failure '{failure.Id}' records mode 'hotfix' but its hotfixPr " +
                        (pr is null ? "is missing" : $"#{pr} does not resolve to a change merged inside the incident window") +
                        " — an unevidenced restore is a hope, not a taxonomy entry",
                        [anchorFailure(failure)]));
                    return false;
                }

                return true;
            }

            case "rollback":
            {
                var to = failure.Evidence.RollbackTo;
                if (to is null || !deploymentsById.TryGetValue(to, out var target) ||
                    deployment is null || target.DeployedAt >= deployment.DeployedAt ||
                    target.TargetSha == deployment.TargetSha)
                {
                    findings.Add(new DoraFinding(
                        "dora/unevidenced-failure", "error",
                        $"failure '{failure.Id}' records mode 'rollback' but its rollbackTo " +
                        (to is null ? "is missing" : $"'{to}' is not an earlier deployment of a different target sha") +
                        " — an unevidenced restore is a hope, not a taxonomy entry",
                        [anchorFailure(failure)]));
                    return false;
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static IReadOnlyList<DoraPeriodRow> BuildSeries(
        DoraConfig config,
        IReadOnlyList<DoraDeployment> active,
        Dictionary<int, List<DoraDeployment>> shipsByPr,
        Dictionary<int, DoraChange> changesByPr,
        IReadOnlyList<DoraFailure> verifiedFailures)
    {
        string PeriodOf(DateTimeOffset timestamp) => config.Period switch
        {
            DoraPeriod.Day => timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DoraPeriod.Month => timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => IsoWeekMonday(timestamp),
        };

        var deploymentsPerPeriod = active
            .GroupBy(d => PeriodOf(d.DeployedAt), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // lead time: a change is anchored to the FIRST deployment shipping it
        var leadSamples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var changesPerPeriod = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in shipsByPr)
        {
            if (!changesByPr.TryGetValue(group.Key, out var change))
            {
                continue; // dangling pr — already flagged by the join rules
            }

            var anchor = group.Value.OrderBy(d => d.DeployedAt).First();
            var period = PeriodOf(anchor.DeployedAt);
            changesPerPeriod[period] = changesPerPeriod.GetValueOrDefault(period) + 1;
            if (!leadSamples.TryGetValue(period, out var samples))
            {
                leadSamples[period] = samples = [];
            }

            samples.Add((anchor.DeployedAt - change.MergedAt).TotalHours);
        }

        var failuresPerPeriod = verifiedFailures
            .GroupBy(f => PeriodOf(f.DetectedAt), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var rows = new List<DoraPeriodRow>();
        foreach (var period in deploymentsPerPeriod.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var deployments = deploymentsPerPeriod[period];
            var changes = changesPerPeriod.GetValueOrDefault(period);
            var lead = leadSamples.TryGetValue(period, out var samples) && samples.Count > 0
                ? MedianHours(samples)
                : null;

            var failures = failuresPerPeriod.GetValueOrDefault(period) ?? [];
            var rate = failures.Length == 0
                ? 0.0
                : Round1((double)failures.Length / deployments * 100.0);

            var mttr = failures.Length == 0
                ? null
                : MedianHours(failures.Select(f => (f.RestoredAt - f.DetectedAt).TotalHours).ToArray());

            rows.Add(new DoraPeriodRow
            {
                Period = period,
                Deployments = deployments,
                Changes = changes,
                LeadTimeHours = lead,
                ChangeFailureRate = rate,
                MttrHours = mttr,
            });
        }

        return rows;
    }

    /// <summary>ISO-week key: the Monday of the week the timestamp falls in.</summary>
    private static string IsoWeekMonday(DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime;
        var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysFromMonday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static double? MedianHours(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var ordered = samples.OrderBy(s => s).ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0;
        return Round1(median);
    }

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    // ── the loud export reader ─────────────────────────────────────────────

    private static JsonElement ReadArray(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"dora events export needs a \"{name}\" array: {path}");
        }

        return element;
    }

    private static string RequireString(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{context} needs a non-empty \"{name}\"");
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        return null;
    }

    private static int? ReadOptionalInt(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new InvalidOperationException($"failure '{context}' has an invalid \"{name}\" — it must be an integer PR number");
        }

        return result;
    }

    private static IReadOnlyList<int> ReadInts(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{context} needs a \"{name}\" array — the PRs the deployment ships (empty is legal: a verified-empty direct commit)");
        }

        var list = new List<int>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var number))
            {
                throw new InvalidOperationException($"{context} has a non-integer \"{name}\" entry — PR numbers are integers");
            }

            list.Add(number);
        }

        return list;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{context} needs a \"{name}\" — timestamps are ISO 8601 UTC, e.g. 2025-10-26T16:57:26Z");
        }

        var text = value.GetString()!;
        if (!DateTimeOffset.TryParseExact(
                text,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidOperationException(
                $"{context} has an invalid \"{name}\" '{text}' — timestamps are ISO 8601 UTC, e.g. 2025-10-26T16:57:26Z");
        }

        return timestamp;
    }
}
