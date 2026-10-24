using System.Text.RegularExpressions;

namespace Sfg.Toolkit.MigrationLint;

/// <summary>
/// The expand-migrate-contract linter (#67): classifies every statement of the
/// committed migration history and checks the contract steps against the
/// committed gates ledger. Removal is a measurement, not a date: a drop needs
/// a recorded window (expanded, then cold) and a gate pass, and it happens
/// deliberately late — after the cold date. Findings carry file:line evidence.
/// </summary>
public static partial class MigrationLintAnalyzer
{
    [GeneratedRegex(@"\w+")]
    private static partial Regex WordPattern();

    public static MigrationLintReport Analyze(MigrationLintConfig config, IReadOnlyList<MigrationFile> migrations)
    {
        var gates = config.LoadGates();
        var findings = new List<MigrationFinding>();

        NaiveBackfillBites(migrations, findings);
        DestructiveFirstBites(migrations, findings);
        ContractStepsAreGated(config, migrations, gates, findings);
        DropsAreIsolated(migrations, findings);

        findings.Sort(MigrationFinding.Comparer);
        return new MigrationLintReport
        {
            Migrations = migrations,
            Gates = gates,
            GatesName = Path.GetFileName(config.GatesFullPath),
            Findings = findings,
            Cap = config.MaxFindings,
        };
    }

    // --- naive backfill --------------------------------------------------------

    /// <summary>An unbounded UPDATE (no WHERE) is the naive backfill: it fails in
    /// production via lock escalation, WAL flooding, and replication lag — a
    /// backfill is an engineered batch loop, not one big statement.</summary>
    private static void NaiveBackfillBites(IReadOnlyList<MigrationFile> migrations, List<MigrationFinding> findings)
    {
        foreach (var migration in migrations)
        {
            foreach (var statement in migration.Statements)
            {
                if (statement.Method != "Sql" || statement.SqlText is not { } sql)
                {
                    continue;
                }

                if (sql.Contains("update", StringComparison.OrdinalIgnoreCase) &&
                    !sql.Contains("where", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new MigrationFinding(
                        "migration/naive-backfill",
                        "error",
                        $"naive backfill — UPDATE without a WHERE clause in '{migration.FileName}' fails in production via " +
                        "lock escalation, WAL flooding, and replication lag; batch by key range with a WHERE, throttle, and checkpoint",
                        [statement.Anchor]));
                }
            }
        }
    }

    // --- destructive-first ordering --------------------------------------------

    /// <summary>Contract before expand is the destructive order: dropping an
    /// object and then touching it again — in the same migration or in a later
    /// one — means the removal happened before its replacement was in place.</summary>
    private static void DestructiveFirstBites(IReadOnlyList<MigrationFile> migrations, List<MigrationFinding> findings)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            var migration = migrations[i];
            var statements = migration.Statements;
            for (var s = 0; s < statements.Count; s++)
            {
                var drop = statements[s];
                if (drop.Method is not ("DropColumn" or "DropTable"))
                {
                    continue;
                }

                // intra-file: a later statement in the same migration references
                // the dropped object (Sql word-match, or the column re-added)
                for (var t = s + 1; t < statements.Count; t++)
                {
                    var later = statements[t];
                    var references = later.Method == "Sql" && later.SqlText is { } sql &&
                                     Words(sql).Contains(ShortName(drop.Object), StringComparer.OrdinalIgnoreCase);
                    if (references)
                    {
                        findings.Add(new MigrationFinding(
                            "migration/destructive-first",
                            "error",
                            $"the migration drops '{drop.Object}' and then references it — expand before contract: " +
                            $"the drop is the last statement that ever touches the object",
                            [later.Anchor, drop.Anchor]));
                    }
                    else if (later.Method == "AddColumn" && later.Object == drop.Object)
                    {
                        findings.Add(new MigrationFinding(
                            "migration/destructive-first",
                            "error",
                            $"the migration drops '{drop.Object}' and then adds it back — expand before contract: " +
                            "add the replacement before removing the original",
                            [later.Anchor, drop.Anchor]));
                    }
                }

                // inter-file: the dropped object is re-added in a later migration
                for (var j = i + 1; j < migrations.Count; j++)
                {
                    foreach (var later in migrations[j].Statements)
                    {
                        if (later.Method == "AddColumn" && later.Object == drop.Object)
                        {
                            findings.Add(new MigrationFinding(
                                "migration/destructive-first",
                                "error",
                                $"column '{drop.Object}' is dropped in '{migration.FileName}' but added back in " +
                                $"'{migrations[j].FileName}' — contract before expand is the destructive order; " +
                                "expand first, measure, then contract",
                                [drop.Anchor, later.Anchor]));
                        }
                    }
                }
            }
        }
    }

    // --- contract steps are gated, windows are measured, drops are late --------

    private static void ContractStepsAreGated(
        MigrationLintConfig config,
        IReadOnlyList<MigrationFile> migrations,
        GatesLedger? gates,
        List<MigrationFinding> findings)
    {
        foreach (var migration in migrations)
        {
            foreach (var statement in migration.Statements)
            {
                if (!statement.RemovesEmittedObject)
                {
                    continue;
                }

                if (gates is null)
                {
                    findings.Add(new MigrationFinding(
                        "migration/contract-without-gate",
                        "error",
                        $"contract step '{statement.Method} {statement.Object}' in '{migration.FileName}' has no " +
                        $"compatibility-gate ledger ({config.GatesPath} is absent) — the gate fails builds, not reviews: " +
                        "record the pass (gate, expandedAt, coldAt, consumers) before removing an emitted object",
                        [statement.Anchor]));
                    continue;
                }

                var pass = gates.PassOf(statement.Object);
                if (pass is null)
                {
                    findings.Add(new MigrationFinding(
                        "migration/contract-without-gate",
                        "error",
                        $"contract step '{statement.Method} {statement.Object}' in '{migration.FileName}' has no gate pass " +
                        $"in {Path.GetFileName(gates.Path)} — the gate fails builds, not reviews: record the pass " +
                        "(gate, expandedAt, coldAt, consumers) before removing an emitted object",
                        [statement.Anchor, gates.Anchor($"\"{statement.Object}\"")]));
                    continue;
                }

                // the window is a measurement, not a date
                if (pass.ExpandedAt is null || pass.ColdAt is null)
                {
                    findings.Add(new MigrationFinding(
                        "migration/removal-without-window",
                        "error",
                        $"removal of '{statement.Object}' in '{migration.FileName}' leans on a pass with no measured window — " +
                        $"the pass records expandedAt {Date(pass.ExpandedAt)}, coldAt {Date(pass.ColdAt)}; a removal needs " +
                        "the measured window ('old column served zero reads for seven consecutive days'), not a date",
                        [statement.Anchor, gates.Anchor($"\"{pass.Object}\"")]));
                }
                else if (pass.ColdAt.Value < pass.ExpandedAt.Value)
                {
                    findings.Add(new MigrationFinding(
                        "migration/removal-without-window",
                        "error",
                        $"removal of '{statement.Object}' in '{migration.FileName}' leans on an inverted window — coldAt " +
                        $"{pass.ColdAt:yyyy-MM-dd} precedes expandedAt {pass.ExpandedAt:yyyy-MM-dd}; the window is a " +
                        "measurement and it runs forward",
                        [statement.Anchor, gates.Anchor($"\"{pass.Object}\"")]));
                }
                else if (migration.Date < pass.ColdAt.Value)
                {
                    findings.Add(new MigrationFinding(
                        "migration/removal-before-cold",
                        "error",
                        $"removal of '{statement.Object}' runs at {migration.Date:yyyy-MM-dd} but its ledger pass measured " +
                        $"it cold at {pass.ColdAt:yyyy-MM-dd} — the drop is deliberately late: it happens after the old " +
                        "column served zero reads for the full window",
                        [statement.Anchor, gates.Anchor($"\"{pass.Object}\"")]));
                }
            }
        }
    }

    // --- one migration, one revert ----------------------------------------------

    /// <summary>A drop mixed with expand or migrate statements cannot be reverted
    /// on its own — a contract step is its own migration and its own PR,
    /// deliberately late.</summary>
    private static void DropsAreIsolated(IReadOnlyList<MigrationFile> migrations, List<MigrationFinding> findings)
    {
        foreach (var migration in migrations)
        {
            var contract = migration.Statements.FirstOrDefault(s => s.Kind == MigrationKind.Contract);
            if (contract is null || migration.Count(MigrationKind.Expand) + migration.Count(MigrationKind.Migrate) == 0)
            {
                continue;
            }

            findings.Add(new MigrationFinding(
                "migration/drop-not-isolated",
                "warning",
                $"'{migration.FileName}' mixes contract with expand/migrate statements — one migration, one revert: " +
                "a drop is its own migration and its own PR, deliberately late",
                [contract.Anchor]));
        }
    }

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "none";

    private static string ShortName(string objectName) => objectName.Contains('.') ? objectName[(objectName.LastIndexOf('.') + 1)..] : objectName;

    private static string[] Words(string sql) => WordPattern().Matches(sql).Select(m => m.Value).ToArray();
}
