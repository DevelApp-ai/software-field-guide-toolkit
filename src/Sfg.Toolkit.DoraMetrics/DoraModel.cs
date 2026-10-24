namespace Sfg.Toolkit.DoraMetrics;

/// <summary>The analysis period the four keys are trended over (#68): the
/// series is per day, ISO week (Monday-anchored), or month.</summary>
public enum DoraPeriod
{
    Day,
    Week,
    Month,
}

/// <summary>One deployment from the recorded export: a release of the target
/// sha to an environment, carrying the PRs its compare range says it ships.</summary>
public sealed record DoraDeployment
{
    public required string Id { get; init; }

    public required string Environment { get; init; }

    public required DateTimeOffset DeployedAt { get; init; }

    public required string TargetSha { get; init; }

    /// <summary>The PRs the deployment ships — verified against the changes,
    /// never trusted. Empty is legal (a verified-empty direct commit).</summary>
    public required IReadOnlyList<int> Prs { get; init; }

    public required int Line { get; init; }
}

/// <summary>One merged change from the recorded export.</summary>
public sealed record DoraChange
{
    public required int Pr { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset MergedAt { get; init; }

    public required string MergeSha { get; init; }

    public required int Line { get; init; }
}

/// <summary>One CI run observing a sha. Attempts are de-duplicated by
/// (workflow, headSha) — one attempt per workflow per sha, silent.</summary>
public sealed record DoraBuild
{
    public required int Run { get; init; }

    public required string Workflow { get; init; }

    public required string HeadSha { get; init; }

    /// <summary>The run's conclusion as recorded ("success", "failure", ...).</summary>
    public required string Conclusion { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>One recorded failure incident, with the taxonomy evidence that
/// names how service was restored — revert, rollback, or hotfix.</summary>
public sealed record DoraFailure
{
    public required string Id { get; init; }

    /// <summary>revert | rollback | hotfix — anything else is unknown and flagged.</summary>
    public required string Mode { get; init; }

    public required string Deployment { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    public required DateTimeOffset RestoredAt { get; init; }

    /// <summary>The taxonomy evidence: revertPr / hotfixPr (a change number) or
    /// rollbackTo (a deployment id) — checked, never trusted.</summary>
    public required DoraFailureEvidence Evidence { get; init; }

    public required int Line { get; init; }
}

/// <summary>The evidence object of a failure — whichever fields the mode needs.</summary>
public sealed record DoraFailureEvidence
{
    public int? RevertPr { get; init; }

    public int? HotfixPr { get; init; }

    public string? RollbackTo { get; init; }
}

/// <summary>A finding, always with file:line evidence into the export.</summary>
public sealed record DoraFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
{
    public static readonly IComparer<DoraFinding> Comparer = Comparer<DoraFinding>.Create((left, right) =>
    {
        var severity = string.CompareOrdinal(left.Severity, right.Severity);
        return severity != 0 ? severity : string.CompareOrdinal(left.Rule, right.Rule) is var rule && rule != 0
            ? rule
            : string.CompareOrdinal(left.Message, right.Message);
    });
}

/// <summary>One documented join and how many of its references the extractor
/// verified against the export — the four keys are only as good as their joins.</summary>
public sealed record DoraJoinStat(string Join, string Description, int Verified, int Total, string Rule)
{
    public bool Clean => Verified == Total;
}

/// <summary>One period of the four-key series. Trends, not scores: numbers
/// against a definition version, never a benchmark label.</summary>
public sealed record DoraPeriodRow
{
    /// <summary>The period key: yyyy-MM-dd (day), ISO-week Monday (week), yyyy-MM (month).</summary>
    public required string Period { get; init; }

    /// <summary>Deployment frequency: deployments in the period, re-runs excluded.</summary>
    public required int Deployments { get; init; }

    /// <summary>Changes first shipped by a deployment in this period.</summary>
    public required int Changes { get; init; }

    /// <summary>Lead time for changes: median hours from merge to first
    /// deployment shipping the change, 1 decimal. Null when nothing shipped.</summary>
    public double? LeadTimeHours { get; init; }

    /// <summary>Change failure rate: verified failures detected in the period
    /// over deployments in the period, percent, 1 decimal.</summary>
    public required double ChangeFailureRate { get; init; }

    /// <summary>Mean time to restore: median hours from detected to restored,
    /// 1 decimal. Null when no verified failure was detected in the period.</summary>
    public double? MttrHours { get; init; }
}

/// <summary>The DORA board (#68): the verified export, the verified joins, the
/// four-key series, and the findings — errors first, deterministic order.</summary>
public sealed class DoraReport
{
    public required IReadOnlyList<DoraDeployment> Deployments { get; init; }

    public required IReadOnlyList<DoraChange> Changes { get; init; }

    public required IReadOnlyList<DoraBuild> Builds { get; init; }

    public required IReadOnlyList<DoraFailure> Failures { get; init; }

    /// <summary>Deployment ids excluded from the counts — the later half of a
    /// same-environment, same-target-sha pair (a re-run mislabeled as a new deployment).</summary>
    public required IReadOnlyList<string> ExcludedDeployments { get; init; }

    public required IReadOnlyList<DoraJoinStat> Joins { get; init; }

    public required IReadOnlyList<DoraPeriodRow> Series { get; init; }

    public required IReadOnlyList<DoraFinding> Findings { get; init; }

    public IReadOnlyList<DoraFinding> RenderedFindings => Findings.Take(Cap).ToArray();

    public int Cap { get; init; }

    public int Errors => Findings.Count(f => f.Severity == "error");

    public int Warnings => Findings.Count(f => f.Severity == "warning");

    /// <summary>The recorded source note: repository and window, when present.</summary>
    public string? Repository { get; init; }

    public string? Window { get; init; }
}
