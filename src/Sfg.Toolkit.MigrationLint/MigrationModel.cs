namespace Sfg.Toolkit.MigrationLint;

/// <summary>The classification of one migration statement. Expand is the
/// additive default; migrate rewires data and readers; contract removes or
/// tightens — the step that needs a measured window and a gate pass.</summary>
public enum MigrationKind
{
    Expand,
    Migrate,
    Contract,
}

/// <summary>One migrationBuilder call inside a migration's Up method, with the
/// line-anchored evidence the findings cite.</summary>
public sealed record MigrationStatement
{
    public required MigrationKind Kind { get; init; }

    /// <summary>The EF Core operation (AddColumn, DropColumn, Sql, ...).</summary>
    public required string Method { get; init; }

    /// <summary>The emitted object the statement touches: "Table" or "Table.Column"
    /// for object-level operations, the index/constraint name for the others.</summary>
    public required string Object { get; init; }

    /// <summary>The raw SQL of a Sql statement (string literals only — a
    /// non-literal SQL cannot be judged and is never guessed about).</summary>
    public string? SqlText { get; init; }

    /// <summary>true for the contract steps that remove or tighten an emitted
    /// object (DropColumn, DropTable, AlterColumn with nullable: false) — the
    /// steps a ledger pass must cover. Index and constraint drops are internal
    /// wiring: contract, but never contract-emitted.</summary>
    public bool RemovesEmittedObject { get; init; }

    public required string File { get; init; }

    /// <summary>1-based line of the statement in its file.</summary>
    public int Line { get; init; }

    public string Anchor => $"{File}:{Line}";
}

/// <summary>One committed migration file: machine-ordered by its 14-digit
/// timestamp, its Up method parsed statement by statement.</summary>
public sealed record MigrationFile
{
    /// <summary>The file name, e.g. "20260601100000_RemoveShippingAddress.cs".</summary>
    public required string FileName { get; init; }

    public required string Timestamp { get; init; }

    /// <summary>The date the migration runs at, from the file timestamp.</summary>
    public required DateOnly Date { get; init; }

    public required IReadOnlyList<MigrationStatement> Statements { get; init; }

    public int Count(MigrationKind kind) => Statements.Count(s => s.Kind == kind);
}

/// <summary>A finding, always with file:line evidence.</summary>
public sealed record MigrationFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
{
    public static readonly IComparer<MigrationFinding> Comparer = Comparer<MigrationFinding>.Create((left, right) =>
    {
        var severity = string.CompareOrdinal(left.Severity, right.Severity);
        return severity != 0 ? severity : string.CompareOrdinal(left.Rule, right.Rule) is var rule && rule != 0
            ? rule
            : string.CompareOrdinal(left.Message, right.Message);
    });
}

/// <summary>The linted history: the timeline, the ledger it was checked
/// against, and the findings — errors first, deterministic order.</summary>
public sealed class MigrationLintReport
{
    public required IReadOnlyList<MigrationFile> Migrations { get; init; }

    /// <summary>The gates ledger the contract steps were checked against, or
    /// null when none is committed (legal until something is removed).</summary>
    public required GatesLedger? Gates { get; init; }

    public required string GatesName { get; init; }

    public required IReadOnlyList<MigrationFinding> Findings { get; init; }

    public IReadOnlyList<MigrationFinding> RenderedFindings => Findings.Take(Cap).ToArray();

    public int Cap { get; init; }

    public int Errors => Findings.Count(f => f.Severity == "error");

    public int Warnings => Findings.Count(f => f.Severity == "warning");

    public int StatementCount => Migrations.Sum(m => m.Statements.Count);

    public int Count(MigrationKind kind) => Migrations.Sum(m => m.Count(kind));
}
