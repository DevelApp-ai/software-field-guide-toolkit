# sfg migration-lint

> Book issue #67 Expand-migrate-contract migration linter.

Parses the committed EF Core migration history, classifies every statement expand (additive), migrate (rewiring), or contract (removing/tightening), and checks each contract step against the gates ledger: a removal needs a gate pass with a measured window (expandedAt, then coldAt) and happens deliberately late. Flags destructive-first ordering (drop-then-reference, drop-then-re-add) and naive unbatched backfills. Advisory by default; `--strict` exits non-zero on errors.

## Usage

```bash
# migration linter: expand/migrate/contract against the gates ledger
sfg migration-lint /path/to/app --out migration-lint
# migration-lint also accepts --config migration-lint.json (see below); it
#   defaults to <root>/migration-lint.json when the file exists, and --gates
#   to judge against a different gates ledger (e.g. a seeded broken one);
#   --strict exits 1 on error-severity findings (default: advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`migration-lint` writes the migration board:

- `migration-lint.md` — the human board: the migration timeline (one row per file, classified expand/migrate/contract), the compatibility windows table (one row per gate pass: gate, expandedAt, coldAt, consumers, and the drop date), and findings sorted errors-first with `file:line` citations on both the migration and the ledger. A clean history renders the explicit `✅ Clean history` line.
- `migration-lint.json` — the machine summary: generator, migrations, gates, findings with rule/severity/message/evidence, error and warning counts.

## Inputs

## Migration config and gates ledger (`migration-lint.json`, `migration-gates.json`, the migration linter's inputs)

`migration-lint.json` is optional — both keys default as shown, `maxFindings` caps the rendered findings list in both boards (the `errors`/`warnings` counts always cover every finding):

```json
{
  "migrationsDir": "Migrations",
  "gates": "migration-gates.json",
  "maxFindings": 50
}
```

`migration-gates.json` records one pass per removed object — `object` is `Table.Column`, `gate` names the compatibility gate that was opened, `expandedAt`/`coldAt` are the measured window edges (the date the replacement went live, and the date the old column was observed serving zero reads), and `consumers` names the consumers that were confirmed rewired before the drop. The ledger is absent-or-legal until a removal fires — then its absence is itself the finding:

```json
{
  "passes": [
    {
      "object": "Orders.ShippingAddress",
      "gate": "forward-compat",
      "expandedAt": "2026-03-01",
      "coldAt": "2026-05-25",
      "consumers": ["ShippingService", "LegacyExportJob"]
    }
  ]
}
```

Malformed ledgers are loud errors (objects must be `Table.Column`, dates must be `yyyy-MM-dd`, passes must be unique, consumers non-empty) — never a guess. `--gates` judges the same history against a different ledger without touching the committed one.

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `migration/naive-backfill` | error | a `Sql` backfill `UPDATE` carries no `WHERE` — the batchless rewrite escalates locks, floods the WAL, and lags replication |
| `migration/destructive-first` | error | a migration drops an object and then references it (or re-adds it), or a later migration re-adds a dropped column — expand before contract |
| `migration/contract-without-gate` | error | a `DropColumn`/`DropTable`/tightening `AlterColumn` removes an emitted object with no gates ledger or no pass for it |
| `migration/removal-without-window` | error | a pass leans on a missing or inverted window — no `expandedAt`/`coldAt`, or `coldAt` before `expandedAt`; a removal needs the measured window, not a date |
| `migration/removal-before-cold` | error | the drop runs before the pass measured the old column cold — the drop is deliberately late, after the window closed |
| `migration/drop-not-isolated` | warning | a contract migration mixes expand/migrate statements — one migration, one revert |

## Honest status

- **`migration-lint` reads EF Core migration files, not a compiled model.** The parser is syntax-level: EF Core file names must be `<14-digit-timestamp>_<Name>.cs`, duplicate timestamps and missing `Up` methods are loud errors, and **unknown `migrationBuilder` calls fail loudly rather than being guessed** (known set: `CreateTable`, `DropTable`, `RenameTable`, `AddColumn`, `DropColumn`, `AlterColumn`, `RenameColumn`, `CreateIndex`, `DropIndex`, `AddForeignKey`, `DropForeignKey`, `Sql` — e.g. `EnsureSchema` is a loud error until added to the known set). Only the `Up` method's statements are analyzed; the analysis proves the committed history obeys the expand-migrate-contract ordering and the ledger's measured windows — it does not observe the production database or traffic. `expandedAt`/`coldAt` are dates you attest to in the ledger; the linter checks the history against them, not their provenance.

## In-repo demo

The migration-lint demo is in-repo too: [`demo/MigrationApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a five-migration EF Core estate that walks the expand → backfill → contract sequence by the book — `Orders.ShippingAddress` is replaced by `DeliveryAddress` with a batched, gated, measured window (expanded 2026-03-01, cold 2026-05-25, dropped 2026-06-01, isolated in its own migration) — plus five deliberately broken variants: a naive unbatched backfill (`migration/naive-backfill`), a drop-then-archive that references the dropped column (`migration/destructive-first` + `migration/drop-not-isolated`), an ungated removal of `Orders.LegacyTotal` (`migration/contract-without-gate`), an unmeasured window (no `coldAt` — `migration/removal-without-window`), and a rushed window whose drop runs before the column measured cold (`migration/removal-before-cold`). CI runs the control clean and byte-identical on regeneration, then asserts each variant bites under `--strict` with its cited evidence — the #67 done-when: the control stays silent, each seeded violation is caught by construction, deterministically.
