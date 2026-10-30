namespace Sfg.Toolkit.ReviewCalib;

/// <summary>One seeded defect inside a review row: the defect the queue
/// planted so the reviewer's decision can be scored against a known
/// expectation — the calibration instrument.</summary>
public sealed record ReviewCalibSeed
{
    /// <summary>The defect the row carries, e.g. seed-01.</summary>
    public required string SeedId { get; init; }

    /// <summary>reject | edit — the decision a calibrated reviewer is
    /// expected to produce on this defect. Anything but a clean approve
    /// counts as a catch; a clean approve is a miss.</summary>
    public required string ExpectedDecision { get; init; }
}

/// <summary>One recorded review decision from the export.</summary>
public sealed record ReviewCalibReview
{
    public required string Id { get; init; }

    /// <summary>The queue the review sat in, e.g. checkout.</summary>
    public required string Queue { get; init; }

    /// <summary>The reviewer's role, e.g. senior, junior — catch rates are
    /// measured per role, never per named person.</summary>
    public required string ReviewerRole { get; init; }

    /// <summary>The reviewer's recorded name — rendered as evidence, never
    /// ranked.</summary>
    public required string Reviewer { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>approve | edit | reject | escalate — the recorded decision.</summary>
    public required string Decision { get; init; }

    /// <summary>Edits the reviewer made before deciding — an examination
    /// leaves traces.</summary>
    public required int EditsBeforeDecision { get; init; }

    /// <summary>The recorded judge's verdict on the same row, when the row
    /// carries one — null is legal (not every row is judged).</summary>
    public bool? JudgeAgrees { get; init; }

    /// <summary>The seeded defect this row carries, null for ordinary rows.</summary>
    public ReviewCalibSeed? Seeded { get; init; }

    public double DecisionSeconds => (DecidedAt - SubmittedAt).TotalSeconds;

    public bool CaughtSeed => Seeded is not null && Decision != "approve";
}

/// <summary>A finding, always with file:line evidence into the export.</summary>
public sealed record ReviewCalibFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
{
    public static readonly IComparer<ReviewCalibFinding> Comparer = Comparer<ReviewCalibFinding>.Create((left, right) =>
    {
        var severity = string.CompareOrdinal(left.Severity, right.Severity);
        return severity != 0 ? severity : string.CompareOrdinal(left.Rule, right.Rule) is var rule && rule != 0
            ? rule
            : string.CompareOrdinal(left.Message, right.Message);
    });
}

/// <summary>Catch-rate evidence for one role: how the role performed against
/// seeded defects. A calibration instrument, never a leaderboard — roles are
/// rendered in ordinal order and named people are never ranked.</summary>
public sealed record ReviewCalibRoleRow
{
    public required string Role { get; init; }

    public required int Reviews { get; init; }

    public required int Seeded { get; init; }

    public required int Caught { get; init; }

    /// <summary>Caught over seeded, as a fraction — null when the role saw
    /// no seeded defect.</summary>
    public double? CatchRate { get; init; }
}

/// <summary>Approval telemetry for one queue: what the queue decided, how
/// often it approved, and how long a decision took.</summary>
public sealed record ReviewCalibQueueRow
{
    public required string Queue { get; init; }

    public required int Reviews { get; init; }

    public required int Approves { get; init; }

    public required int Edits { get; init; }

    public required int Rejects { get; init; }

    public required int Escalations { get; init; }

    /// <summary>Approves over reviews, as a fraction.</summary>
    public required double ApprovalRate { get; init; }

    /// <summary>Median seconds from submitted to decided, 1 decimal.</summary>
    public required double MedianSeconds { get; init; }

    public required int Seeded { get; init; }

    public required int Caught { get; init; }

    /// <summary>Caught over seeded, as a fraction — null when the queue saw
    /// no seeded defect.</summary>
    public double? CatchRate { get; init; }

    /// <summary>The most recent seeded decision in the queue, null when
    /// there was none — the staleness evidence.</summary>
    public DateTimeOffset? LastSeededDecidedAt { get; init; }
}

/// <summary>The rung-evidence export for one queue — the evidence an
/// earned-autonomy rung claim must cite. The ladder is earned by people;
/// this row only records what the export shows.</summary>
public sealed record ReviewCalibRungRow
{
    public required string Queue { get; init; }

    public required int Seeded { get; init; }

    public required int Caught { get; init; }

    public double? CatchRate { get; init; }

    public required double ApprovalRate { get; init; }

    public required double MedianSeconds { get; init; }

    /// <summary>Human-vs-judge agreement on this queue's judged rows —
    /// null when the queue has none.</summary>
    public double? JudgeAgreement { get; init; }

    public DateTimeOffset? LastSeededDecidedAt { get; init; }
}

/// <summary>The reviewer calibration board (#77): the recorded export, the
/// catch-rate and telemetry rows, the judge calibration, the rung evidence,
/// and the findings — errors first, deterministic order.</summary>
public sealed class ReviewCalibReport
{
    public required IReadOnlyList<ReviewCalibReview> Reviews { get; init; }

    public required IReadOnlyList<ReviewCalibRoleRow> Roles { get; init; }

    public required IReadOnlyList<ReviewCalibQueueRow> Queues { get; init; }

    public required IReadOnlyList<ReviewCalibRungRow> Rungs { get; init; }

    public required int SeededCount { get; init; }

    public required int CaughtCount { get; init; }

    /// <summary>Human-vs-judge agreement over the judged rows — null when
    /// the export carries none.</summary>
    public double? Agreement { get; init; }

    /// <summary>Rows carrying a recorded judge verdict.</summary>
    public required int JudgeSample { get; init; }

    public required IReadOnlyList<string> Disagreements { get; init; }

    public required IReadOnlyList<ReviewCalibFinding> Findings { get; init; }

    public IReadOnlyList<ReviewCalibFinding> RenderedFindings => Findings.Take(Cap).ToArray();

    public int Cap { get; init; }

    public int Errors => Findings.Count(f => f.Severity == "error");

    public int Warnings => Findings.Count(f => f.Severity == "warning");

    /// <summary>The recorded analysis anchor (source.today) — the harness is
    /// deterministic and never reads the wall clock.</summary>
    public required string Today { get; init; }

    public string? Repository { get; init; }

    public string? Window { get; init; }
}