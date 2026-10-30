# Software Field Guide Toolkit

Companion tools for the book *The Software Field Guide* (DevelApp-ai/software-field-guide). `sfg` extracts architecture **truth from code** — not from what the diagrams claim — and renders editable, evidence-cited artifacts.

Eleven commands ship today, mapping to book issues [#54](https://github.com/DevelApp-ai/software-field-guide/issues/54), [#55](https://github.com/DevelApp-ai/software-field-guide/issues/55), [#58](https://github.com/DevelApp-ai/software-field-guide/issues/58), [#56](https://github.com/DevelApp-ai/software-field-guide/issues/56), [#60](https://github.com/DevelApp-ai/software-field-guide/issues/60), [#64](https://github.com/DevelApp-ai/software-field-guide/issues/64), [#66](https://github.com/DevelApp-ai/software-field-guide/issues/66), [#67](https://github.com/DevelApp-ai/software-field-guide/issues/67), [#68](https://github.com/DevelApp-ai/software-field-guide/issues/68), [#77](https://github.com/DevelApp-ai/software-field-guide/issues/77), and [#78](https://github.com/DevelApp-ai/software-field-guide/issues/78):

| Command | Book issue | What it does |
| --- | --- | --- |
| `sfg context-map` | #54 Architecture context mapper and boundary doctor | Scans a .NET solution, builds the context dependency graph, and flags reach-arounds, forbidden directions, shared-kernel sprawl, and orphaned seams — every finding with file:line evidence. Emits an editable drawio map in the book's diagram conventions. |
| `sfg seam-ledger` | #55 Extension-point ledger and seam evidence linter | Inventories every seam (interfaces, plugin contracts, event hooks) with consumer counts, flags one-implementation seams as speculative generality, checks versioning and default implementations, and renders the collapse-review checklist. The evidence bar is a required input, never a guess. |
| `sfg tenant-probes` | #58 Multitenant isolation probe generator | Scans an EF Core model (DbContext, `DbSet<T>` entities) and generates a compilable xunit probe suite: cross-tenant read probes, fail-closed save guards, pool-checkout session-leakage checks, and disabled-state fail-closed probes — one `[Fact]` per entity, all citing the scanned file:line inventory. Runs in CI so isolation regresses loudly. |
| `sfg cockpit` | #56 Strangler-fig migration cockpit | Reads the migration route table (`migration.json`), checks it against the routing seam (every declared route wired; no undeclared wires), runs the sunset tripwire (a strangled route whose old-implementation traffic stayed under the retirement threshold for the full sunset window — or the fig is not finished), and generates a compilable shadow-diff xunit suite comparing old and new implementations per declared tolerance. |
| `sfg events` | #60 Event contract conformance harness | Given a message contract (`events.json`), generates a compilable xunit suite that replays chaos-lite against the real consumer: duplicate delivery (exactly-once effects), out-of-order arrival, poison payloads (routed to the dead letters, never a crash), and old-schema producer replay (version-skew tolerance), plus outbox health checks on the producer side (publish waits pending; double dispatch stays idempotent). Seeded faults visibly fail a naive consumer and pass a compliant one — the #60 done-when. |
| `sfg plan-audit` | #64 Plan-conformance auditor | Reconciles the approved spec diff (the intent PR's `.ai`/`.test` changes) against the generated code diff (the implementation PR) through a declared spec-to-code coverage map, and reports the two discrepancy classes — **omission** (an approved spec change no generated change implements) and **unapproved addition** (a generated change with no spec basis) — line-anchored on both sides, ranked errors-first and capped. Advisory by default; `--strict` exits non-zero on errors. LLM-assisted semantic matches are accepted only with valid anchors on both sides and are always labeled. |
| `sfg eval` | #66 LLM evaluation harness runner and baseline gate | Judges a **recorded** run (a committed `results.json`) against the suite's committed labeled set: aggregates per-stage metrics, calibrates the judge against the human-labeled sample (abstains never count as agreement), and gates against both the declared thresholds and the committed baseline — a regression cites the cases that moved, never just the aggregate. Dropped cases, undeclared cases, duplicate results, and stray metrics are loud errors. `--update-baseline` re-baselines deliberately. The harness never calls a model — wiring a live system means producing a results file in the documented format. |
| `sfg migration-lint` | #67 Expand-migrate-contract migration linter | Parses the committed EF Core migration history, classifies every statement expand (additive), migrate (rewiring), or contract (removing/tightening), and checks each contract step against the gates ledger: a removal needs a gate pass with a measured window (expandedAt, then coldAt) and happens deliberately late. Flags destructive-first ordering (drop-then-reference, drop-then-re-add) and naive unbatched backfills. Advisory by default; `--strict` exits non-zero on errors. |
| `sfg dora` | #68 DORA four-key extractor | Extracts the DORA four keys from a **recorded** events export (deployments, changes, builds, failures) — it never calls an API live. Verifies every join the keys depend on (deployment → change → build, failure → restore evidence), flags re-runs mislabeled as new deployments (excluded from every count), and trends deployment frequency, lead time, change failure rate, and restore time per day, ISO week, or month against a versioned definitions file. Trends, not scores. Advisory by default; `--strict` exits non-zero on errors. |
| `sfg review-calib` | #77 Reviewer calibration harness | Measures the humans in the loop from a **recorded** export of review decisions — it never calls an API live and never ranks people. Computes catch rates on seeded defects per role, queue telemetry (approval rate and decision latency side by side — a queue that approves everything in under a second is a broken loop, not a review), and judge agreement on the recorded sample, and renders the rung-evidence table the autonomy ladder is earned on. Calibration instrument, not a leaderboard. Advisory by default; `--strict` exits non-zero on errors. |
| `sfg bias-probe` | #78 Bias probe pack | Extends the #66 case format into a bias probe: counterfactual pairs (the same input with only the marker changed) must record the same dimension score — a deterministic consistency rate, not vibes; cohort-sliced metrics on the same baseline rail (the population is declared by cases, never guessed); refusal asymmetry (should-succeed cases must succeed for every cohort alike, and a must-refuse case that answers fails regardless of cohort); citation balance against declared corpus shares (advisory — the drift is published, an undeclared source cohort is an error); and judge calibration against a human-labeled sample, mandatory — abstentions never count. A regression cites the pairs and cases that moved. Advisory by default; `--strict` exits non-zero on errors. |

The two analysis commands share one syntax-level scan engine, so a seam in the ledger and a contract in the map are the same underlying type. The probe generator, the shadow-diff suite, and the event conformance suite are the tool's outputs that **compile against your code** — that is the point: the probes bite, or the isolation / migration / messaging claim is already broken.

## Install

```bash
# from a checkout
dotnet pack src/sfg -c Release -o artifacts
dotnet tool install Sfg.Tool --tool-path .tools --add-source artifacts
./.tools/sfg --help
```

Requires the .NET 8 SDK. No NuGet packages are needed for the *analyzed* code — the tool never restores, builds, or runs the target solution.

## Usage

```bash
# context map: drawio + json + findings report
sfg context-map /path/to/solution

# seam ledger: json ledger + collapse-review checklist
sfg seam-ledger /path/to/solution

# tenant-isolation probe suite: compilable xunit project
sfg tenant-probes /path/to/multitenant/app --out probes

# strangler-fig cockpit: route board + sunset tripwire + shadow-diff suite
sfg cockpit /path/to/app --out cockpit

# event contract conformance suite: chaos-lite replay against the real consumer
sfg events /path/to/app --out events

# plan-conformance audit: intent diff vs implementation diff, advisory
sfg plan-audit /path/to/estate --spec-diff intent.diff --code-diff implementation.diff

# LLM eval harness: judge a recorded run against the labeled set and baseline
sfg eval /path/to/suite --out eval

# migration linter: expand/migrate/contract against the gates ledger
sfg migration-lint /path/to/app --out migration-lint

# DORA four keys: verified joins, trended series from a recorded export
sfg dora /path/to/app --out dora

# reviewer calibration harness: catch rates and queue telemetry from a recorded export
sfg review-calib /path/to/app --out review-calib

# bias probe pack: counterfactual consistency, cohort slices, refusal asymmetry
sfg bias-probe /path/to/app --out bias-probe

# all accept:
#   --config sfg.json       analysis configuration (see below)
#   --out DIR               output directory (default sfg-out/<command>)
#   --strict                exit 1 if any error-severity finding (context-map, cockpit)
#   --evidence evidence.json seam evidence entries (seam-ledger)
# tenant-probes also accepts --config tenant-probes.json (see below)
# cockpit also accepts --config migration.json (see below); it defaults to
#   <root>/migration.json when the file exists
# events also accepts --config events.json (see below); it defaults to
#   <root>/events.json
# plan-audit also accepts --config plan-audit.json (see below); it defaults to
#   <root>/plan-audit.json, and --semantic matches.json for externally
#   produced (e.g. LLM-assisted) spec-to-code matches; --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# eval also accepts --config eval.json (see below); it defaults to
#   <root>/eval.json, --results results.json to judge a different recorded
#   run, and --update-baseline to rewrite the committed baseline from this
#   run (deliberate re-baselining); --strict exits 1 on error-severity
#   findings (default: advisory, exit 0)
# migration-lint also accepts --config migration-lint.json (see below); it
#   defaults to <root>/migration-lint.json when the file exists, and --gates
#   to judge against a different gates ledger (e.g. a seeded broken one);
#   --strict exits 1 on error-severity findings (default: advisory, exit 0)
# dora also accepts --config dora.json (see below); it defaults to
#   <root>/dora.json when the file exists, and --events to read a different
#   recorded export (e.g. a seeded broken one); --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# review-calib also accepts --config review-calib.json (optional — every key
#   has a default) and --events to read a different recorded export (e.g. a
#   seeded broken one); --strict exits 1 on error-severity findings (default:
#   advisory, exit 0)
# bias-probe also accepts --config bias-probe.json (it defaults to
#   <root>/bias-probe.json), --results to judge a different recorded run
#   (e.g. a seeded broken one), and --update-baseline to rewrite the
#   committed baseline from this run (deliberate re-baselining); --strict
#   exits 1 on error-severity findings (default: advisory, exit 0)
```

### Outputs

`context-map` writes:

- `context-map.drawio` — deterministic drawio context map (house palette: host blue, plugin orange, shared-kernel yellow; green edges clean, red dashed edges forbidden). Same input, byte-identical file — safe to diff and commit.
- `context-map.json` — machine-readable contexts, edges, and findings.
- `findings.md` — findings table sorted errors-first, each row citing `file:line`.

`seam-ledger` writes:

- `seam-ledger.json` — the versioned complexity ledger: one entry per seam with kind (`interface` / `plugin-contract` / `event-hook`), file:line, implementations, consumer contexts, usage sites, default implementation (with registration site), `versioned` flag, speculative-generality flag, and evidence status.
- `collapse-review.md` — the checklist: which seams are **due for a collapse decision** (and why), which to monitor, and which fail the evidence bar.

`tenant-probes` writes a compilable xunit project (run it with `dotnet test`):

- `TenantProbes.csproj` — references your project and the EF Core provider; regenerating is safe, the output is byte-identical for the same scan.
- `ProbeHarness.cs` — owns an in-memory SQLite database, seeds one row per probe tenant per entity, and checks out additional contexts over the same options (the pool-checkout stand-in).
- `CrossTenantReadProbes.cs` — per entity: tenant B's query must return exactly tenant B's rows.
- `SaveGuardProbes.cs` — per entity: saving an entity stamped with a foreign tenant id must throw.
- `PoolCheckoutProbes.cs` — per entity: a fresh context over the same options must not inherit the previous tenant's session state.
- `DisabledStateFailClosedProbes.cs` — per entity: with the tenant context absent, reads return nothing and writes throw (never a fallback to all tenants).
- `manifest.json` — the versioned probe inventory: DbContext (with file:line), every probed entity (name, namespace, DbSet, file:line), config echo.
- `README.md` — what each probe class bites on, and the probed-entity table.

`cockpit` writes the migration board and, for routes with `"shadow": true`, a compilable shadow-diff suite:

- `cockpit.md` — the human board: every route's state, wiring evidence, old/new handlers, traffic history, retirement threshold, and sunset verdict; findings sorted errors-first with `file:line` citations.
- `cockpit.json` — the machine summary: routes, wiring, tripwire verdicts, and findings.
- `cockpit.drawio` — the state map in the book's conventions (strangled green, strangling amber, todo grey; a fired tripwire draws a red dashed stroke).
- `ShadowDiff.csproj` + `ShadowDiffHarness.cs` + `RouteShadowProbes.cs` + `Cases.cs` — one `[Fact]` per shadow route: the same cases through old and new, deep JSON comparison with per-route tolerance (numeric fields keyed by leaf name at any depth; ignored fields skipped at any depth). A drift fails with `SHADOW DRIFT on route '...' case #...`, naming the route, case, path, and both values. Run with `dotnet test`.
- `manifest.json` — the shadow inventory: target project, per-route old/new handlers with file:line, case counts, tolerance echo.
- `README.md` — what the suite bites on, per route.

### Outputs

`events` writes the conformance board and a compilable xunit project (run it with `dotnet test`):

- `events.md` — the human board: every event, its version, case counts, old-schema replay, the scenarios replayed per event, and the conventions verified with `file:line` evidence.
- `events.json` — the machine summary: consumer and outbox declarations, target project, events with case counts.
- `EventConformance.csproj` + `EventConformanceHarness.cs` + `ConsumerProbes.cs` + `OutboxProbes.cs` + `Cases.cs` — per event: duplicate delivery applies the message id exactly once; reversed (out-of-order) delivery still applies every case; a fixed poison corpus (malformed JSON, a non-object message, a type-violating envelope) routes to the dead letters without crashing; and for events declaring a `previousVersion`, old-schema replays are applied or dead-lettered — never lost. With an outbox declared: a publish waits in `Pending`, and a double `MarkDispatched` leaves exactly one `Dispatched` entry and an empty `Pending`. Failures carry `EVENT CONTRACT violated on '...'`, naming the event, the message id, and the broken guarantee.
- `manifest.json` — the versioned inventory: consumer and outbox with member evidence, scenarios, per-event case counts.
- `README.md` — what each probe bites on, per event.

`plan-audit` writes the conformance board:

- `plan-audit.md` — the human board: the approved spec change and the generated change side by side (each changed file with its line anchor and mapped area), the matches that reconcile them (machine matches from the coverage map; semantic matches labeled with their note), and the findings sorted errors-first with `file:line` citations on both sides.
- `plan-audit.json` — the machine summary: spec and code files with anchors and areas, matches with provenance, findings with rule/severity/anchors, error and warning counts.

`eval` writes the eval board:

- `eval.md` — the human board: per-stage metric table (observed vs threshold vs baseline, with verdict), judge agreement against the human-labeled sample (abstains never count; disagreements name the cases), and findings sorted errors-first with `file:line` citations — a regression cites the cases that moved.
- `eval.json` — the machine summary: run name, per-metric observed/threshold/baseline, agreement numbers, findings with rule/severity/message/evidence, error and warning counts.
- `baseline.json` (via `--update-baseline`) — the committed baseline: per-metric aggregate values plus per-case scores, so a future regression can cite exactly the cases that moved. Deterministic, ordinal-sorted, no timestamps.

`migration-lint` writes the migration board:

- `migration-lint.md` — the human board: the migration timeline (one row per file, classified expand/migrate/contract), the compatibility windows table (one row per gate pass: gate, expandedAt, coldAt, consumers, and the drop date), and findings sorted errors-first with `file:line` citations on both the migration and the ledger. A clean history renders the explicit `✅ Clean history` line.
- `migration-lint.json` — the machine summary: generator, migrations, gates, findings with rule/severity/message/evidence, error and warning counts.

`dora` writes the four-key board:

- `dora.md` — the human board: the joins table (every join the four keys depend on, with verified/total counts and the rule that guards it), the per-period time series (deployments, changes shipped, lead time, failure rate, restore time), the excluded re-runs, and findings sorted errors-first with `file:line` citations into the export. A clean export renders the explicit `✅ Clean export` line.
- `dora.json` — the machine summary: generator, definition version, counts, verified joins, the series, findings with rule/severity/message/evidence, error and warning counts.
- `dora-definitions.json` — the versioned definitions file (`dora-definitions-v1`): the four keys, the joins, the failure taxonomy (revert/rollback/hotfix and the evidence each demands), the re-run rule, the periods, and the trend policy. Deterministic and byte-identical on regeneration — commit it next to the export it defines.

`review-calib` writes the calibration board:

- `review-calib.md` — the human board: catch rates by role (reviews, seeded, caught, rate), queue telemetry (approve/edit/reject/escalate counts, approval rate, median decision seconds — the rubber-stamp evidence), judge calibration (agreement on the recorded sample, disagreements named), and the rung-evidence table the autonomy ladder is earned on. A clean export renders the explicit `✅ Clean export` line.
- `review-calib.json` — the machine summary: generator, counts, per-role catch rates, per-queue telemetry, judge agreement, rung evidence, findings with rule/severity/message/evidence, error and warning counts.

`bias-probe` writes the probe board:

- `bias-probe.md` — the human board: the counterfactual consistency table (pair, dimension, both leg scores, verdict — a pair is consistent only when both legs record the same score), the consistency rate against the floor, cohort-sliced metrics (observed vs threshold per cohort, where bias hides from the aggregate), refusal asymmetry (per-cohort refusal rates over the should-succeed cases and the delta against the ceiling), citation balance (observed share vs the declared corpus share), judge calibration (agreement on the human-labeled sample, abstentions never count, disagreements named), and findings sorted errors-first with `file:line` citations. A clean run renders the explicit `✅ Clean run` line.
- `bias-probe.json` — the machine summary: generator, counts, consistency, per-metric aggregates and cohort slices, refusal slices, citation shares, judge calibration, findings with rule/severity/message/evidence, error and warning counts.

### Route table (`migration.json`, the cockpit's required input)

```json
{
  "project": "src/MyWeb/MyWeb.csproj",
  "routeMethod": "MapRoute",
  "defaultRetirementThreshold": 100,
  "defaultSunsetPeriods": 3,
  "routes": [
    {
      "id": "orders.search",
      "state": "strangling",
      "old": "LegacyOrdersSearch",
      "new": "OrdersSearchService",
      "shadow": true,
      "cases": "cases/orders.search.json",
      "tolerance": { "numeric": { "total": 0.01 }, "ignore": [ "servedBy", "elapsedMs" ] },
      "traffic": [ 4200, 3100, 650 ]
    },
    {
      "id": "shipping.quote",
      "state": "strangled",
      "old": "LegacyShippingQuote",
      "new": "ShippingQuoteService",
      "traffic": [ 4200, 210, 90, 0, 0, 0 ],
      "retirementThreshold": 100,
      "sunsetPeriods": 3
    }
  ]
}
```

`state` is `todo`, `strangling`, or `strangled` (strangling/strangled need `old` + `new`; shadow routes need `old` + `new` + `cases`). `traffic` is the old implementation's request count per period, oldest first. Only `routes` is required; `project` is optional (defaults to the shadow handlers' single distinct project), `routeMethod` defaults to `MapRoute`. The pivot scan honors only string-literal route ids — dynamic ids are skipped loudly by omission, never guessed.

### Configuration (`tenant-probes.json`, all keys optional)

```json
{
  "project": "src/MyApp/MyApp.csproj",
  "dbContext": "AppDbContext",
  "tenantProperty": "TenantId",
  "contextTenantProperty": "CurrentTenantId",
  "tenantA": "tenant-a",
  "tenantB": "tenant-b",
  "providerPackage": "Microsoft.EntityFrameworkCore.Sqlite",
  "providerVersion": "8.0.11"
}
```

Defaults: the DbContext is auto-detected when exactly one exists under the root (otherwise configure `dbContext`); the target project is the DbContext's nearest csproj; tenant discriminator `TenantId`, ambient tenant property `CurrentTenantId`, probe tenants `tenant-a`/`tenant-b`, provider SQLite 8.0.11.

### Conventions the probe target must satisfy

- the DbContext constructor accepts `DbContextOptions`,
- the DbContext exposes a **settable** ambient-tenant property (`CurrentTenantId` by default) — the session state the probes manipulate,
- every probed entity has a settable tenant property (`TenantId` by default) and a parameterless constructor.

If a convention is missing the generated suite fails to compile — deliberately loud, not a silent skip.

### Event contract (`events.json`, the events harness's required input)

```json
{
  "project": "src/MyApp/MyApp.csproj",
  "consumer": "OrderEventsConsumer",
  "outbox": "Outbox",
  "events": [
    {
      "id": "order.placed",
      "version": 2,
      "cases": "cases/order.placed.json",
      "previousVersion": 1,
      "skewCases": "cases/order.placed.v1.json"
    },
    {
      "id": "invoice.issued",
      "version": 1,
      "cases": "cases/invoice.issued.json"
    }
  ]
}
```

`cases` (and `skewCases`, required when `previousVersion` is declared) are JSON files of payload arrays, replayed verbatim. `previousVersion` must be older than `version` — the version-skew scenario replays an old-schema producer, never a guess about ordering. Duplicate event ids, missing case files, and skew without a previous version fail loudly at generation time.

### Plan-audit map (`plan-audit.json`, the plan auditor's required input)

```json
{
  "project": "src/QuoteTree/QuoteTree.csproj",
  "map": [
    { "spec": "specs/quote/", "code": "src/QuoteTree/Quote/" },
    { "spec": "specs/billing/", "code": "src/QuoteTree/Billing/" }
  ],
  "allowedUnmappedCode": [ "src/QuoteTree/QuoteTree.csproj" ]
}
```

`map` declares the spec-to-code coverage: intent files under a `spec` prefix are implemented by code under the paired `code` prefix — that pairing is the machine reconciliation unit. `allowedUnmappedCode` lists code paths that may change without a spec basis (build files, docs). `maxFindings` (default 50) caps the rendered findings; the hidden count is named. Overlapping spec or code prefixes never pass silently — an ambiguous map is a loud error.

The optional `--semantic matches.json` file declares externally produced spec-to-code correspondences (an LLM pass is the expected producer):

```json
{
  "matches": [
    { "spec": "specs/quote/quote-order.ai:11", "code": "src/QuoteTree/Billing/AuditTrail.cs:1",
      "note": "R5's audit trail is implemented by the new AuditTrail class, outside the declared quote-area map." }
  ]
}
```

Every semantic match must anchor a line the spec diff actually changes to a line the code diff actually changes, cite changed files on both sides, and is rendered labeled `semantic` — never a silent guess.

### Eval suite (`eval.json`, the eval harness's required input)

```json
{
  "name": "docs-assistant-eval",
  "judge": "rule-based-judge-v1",
  "stages": [
    { "id": "retrieval", "metrics": ["recallAt3"] },
    { "id": "generation", "metrics": ["faithfulness", "refusalCorrectness"] }
  ],
  "thresholds": { "recallAt3": 0.8, "faithfulness": 0.9, "refusalCorrectness": 1.0 },
  "cases": "cases.json",
  "results": "results.json",
  "baseline": "baseline.json"
}
```

`stages` declares the pipeline stages and the metrics their cases are scored by — metric names are global (thresholds and baselines key on them). `thresholds` are the floor the suite declared; a threshold for an undeclared metric never passes silently. Paths resolve relative to the config's directory. `judge` is the judge configuration id, published with every report.

`cases.json` is the labeled set, committed with the suite:

```json
{ "cases": [
  { "id": "r1", "stage": "retrieval", "input": "query: ...", "expected": "docs/...", "human": "correct" }
] }
```

Cases carrying a `human` label (`correct`/`incorrect`) form the human-labeled sample the judge is calibrated against.

`results.json` is the recorded run to judge — the system under evaluation produces it (wiring a live system means producing this file in this format; the harness never calls a model):

```json
{ "run": "docs-assistant-v3", "results": [
  { "id": "r1", "predicted": "docs/...", "judge": "correct", "scores": { "recallAt3": 1.0 } }
] }
```

`judge` per result is the judge's verdict (`correct`/`incorrect`/`abstain`); scores are numbers in [0, 1] keyed by declared metric. `baseline.json` (written by `--update-baseline`) records per-metric aggregate values plus per-case scores, so a regression cites the cases that moved — never just the aggregate.

### Bias probe pack (`bias-probe.json`, the bias probe's required input)

```json
{
  "name": "docs-assistant-bias",
  "judge": "rubric-judge-v2",
  "consistencyFloor": 0.9,
  "refusalDeltaMax": 0.1,
  "citationDeltaMax": 0.15,
  "corpusShares": { "internal": 0.75, "community": 0.25 },
  "metrics": [ { "id": "faithfulness", "threshold": 0.8 } ],
  "pairs": "pairs.json",
  "cases": "cases.json",
  "results": "results.json",
  "baseline": "baseline.json"
}
```

`consistencyFloor` is the absolute floor for the counterfactual consistency rate; `refusalDeltaMax` is the ceiling for the refusal-rate delta across cohorts; `citationDeltaMax` is the advisory band for citation share vs `corpusShares` (which must sum to 1). `metrics` declares the cohort-sliced metrics and their thresholds — a case declares which metrics it rides (the population is declared by cases, never guessed). Paths resolve relative to the config's directory.

`pairs.json` is the counterfactual pair pack — the probe's own generator input:

```json
{ "pairs": [
  { "id": "p1", "dimension": "tone", "markerA": "Anna", "markerB": "Ahmed",
    "input": "A customer named {marker} in the {cohort} region asks ... " }
] }
```

The tool materializes case ids `<pairId>:a` / `<pairId>:b` deterministically; a pair is consistent only when both legs record the same score for its `dimension`.

`cases.json` extends the #66 labeled set with `cohort`, optional `shouldSucceed` (true — must answer; false — must refuse), the `metrics` the case rides, and optional `human` labels (the judge-calibration sample — mandatory somewhere in the set). `results.json` extends the #66 recorded run with `refused` and optional `citations: [{ source, cohort }]`; scores are keyed by declared metric or pair dimension. `baseline.json` (written by `--update-baseline`) records the consistency rate with per-pair outcomes plus per-metric, per-cohort aggregates with per-case scores — so every regression cites exactly what moved.

### Migration config and gates ledger (`migration-lint.json`, `migration-gates.json`, the migration linter's inputs)

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

### DORA events export and config (`dora-events.json`, `dora.json`, the extractor's inputs)

The extractor reads a committed, **recorded** export — it never calls an API live; wiring a live system means producing an export in the documented format. `dora.json` is optional (`events` and `period` default as shown; `maxFindings` caps the rendered findings list):

```json
{
  "events": "dora-events.json",
  "period": "week"
}
```

`dora-events.json` records the four event classes the keys join — `deployments` (each with the PRs its compare range says it ships), `changes` (merged PRs with their merge shas), `builds` (CI runs observing shas; attempts deduplicate per workflow per sha), and `failures` (incidents with their restore taxonomy: `revert`/`rollback`/`hotfix` and the evidence each mode demands — a revert or hotfix names the PR merged inside the incident window; a rollback names the earlier deployment of a different target sha):

```json
{
  "deployments": [
    { "id": "v2.1.0", "environment": "release", "deployedAt": "2025-11-04T19:18:16Z",
      "targetSha": "5a6ac5ca352cd3423820e87746e0bfa2fc1a9509", "prs": [22] }
  ],
  "changes": [
    { "pr": 22, "title": "Fix semantic versioning and test plugin loading",
      "mergedAt": "2025-11-04T19:17:18Z", "mergeSha": "5a6ac5ca352cd3423820e87746e0bfa2fc1a9509" }
  ],
  "builds": [
    { "run": 144, "workflow": ".NET CI/CD", "headSha": "5a6ac5ca352cd3423820e87746e0bfa2fc1a9509",
      "conclusion": "success", "completedAt": "2025-11-04T19:18:10Z" }
  ],
  "failures": [
    { "id": "leap-day-theme-liquid-conflict", "mode": "hotfix", "deployment": "v2.0.7",
      "detectedAt": "2025-10-26T21:30:00Z", "restoredAt": "2025-10-27T00:20:40Z",
      "evidence": { "hotfixPr": 18 } }
  ]
}
```

Timestamps are ISO 8601 UTC; a missing array, an invalid timestamp, or a duplicate id is a loud error — never a guess. An empty `prs` list is legal (a verified-empty direct commit). `--events` reads a different recorded export without touching the committed one.

### Conventions the events target must satisfy

- the consumer exposes `Handle(string message)` — one delivery, one call,
- the consumer exposes `AppliedEffects` (`IReadOnlyList<string>` of applied message ids, deduplicated — at-least-once delivery, exactly-once effects),
- the consumer exposes `DeadLetters` (`IReadOnlyList<string>` of raw dead-lettered messages),
- the consumer has a parameterless constructor;
- the outbox (optional) exposes `Publish(string)`, `MarkDispatched(string)`, `Pending` and `Dispatched` (`IReadOnlyList<string>`), and a parameterless constructor.

If a convention is missing the generator fails loudly with file:line evidence — never a silent skip.

### Configuration (`sfg.json`, all keys optional)

```json
{
  "contractNamespaces": [".Contracts"],
  "sharedKernelProjects": [],
  "forbiddenEdges": ["plugin->plugin", "shared-kernel->plugin", "shared-kernel->host"],
  "pluginContractInterfaces": ["IPluginClass", "IPluginModule"],
  "eventContracts": ["IEventHandler"],
  "roles": { "Some.Project": "plugin" }
}
```

Roles are inferred from project names when not set: `SharedKernel`/`Shared` → shared-kernel, `Plugin` → plugin, `Host` → host, `Test` → other (tests are not boundary participants), everything else → other. The `roles` map overrides inference per project name.

### Evidence file (`evidence.json` for `seam-ledger --evidence`)

A seam may only stay open with a stated **variation axis**, **two named teams**, and a **design doc link**:

```json
{
  "IPluginModule": {
    "variationAxis": "which ERP modules ship in a deployment",
    "teams": ["Orders team", "Finance team"],
    "designDoc": "https://github.com/DevelApp-ai/ErpLight/wiki/Plugin-modules"
  }
}
```

Seams without a complete entry are listed under "Evidence bar" — the tool never invents justification.

## Rules

Context map (`findings.md`):

| Rule | Severity | Fires when |
| --- | --- | --- |
| `boundary/direction-violation` | error | a context consumes another across a configured forbidden edge (e.g. plugin → plugin) |
| `boundary/reach-around` | warning | a context uses a public type from another context that sits outside its contract surface |
| `boundary/orphaned-seam` | warning | a published contract has no consumers outside its own context |
| `boundary/shared-kernel-sprawl` | warning | a shared-kernel contract has exactly one consuming context — candidate to move closer to its user |

Seam ledger:

| Check | Meaning |
| --- | --- |
| `speculativeGenerality` | one implementation — the seam may be a guess |
| `collapseReview.due` | seam has ≤1 consumer context and ≤1 implementation: collapse it, publish it deliberately, or delete it (reasons listed per seam) |
| `versioned` | seam surface carries a `Version` member or version-typed contract |
| `defaultImplementation` | implementation registered in DI-style code, else conventional name match (seam minus `I`) — cites the registration file:line |
| `evidenceComplete` | required evidence entry present and complete |

Cockpit:

| Rule | Severity | Fires when |
| --- | --- | --- |
| `migration/unwired-route` | error | a declared route is never wired at the routing seam — the pivot must call `MapRoute("id", ...)` before the migration can claim it |
| `migration/undeclared-route` | warning | a route is wired at the seam but missing from the route table — an undeclared route strangles nothing |
| `migration/sunset-window` | warning | tripwire: a strangled route's old-implementation traffic stayed at or under the retirement threshold for the full sunset window — delete the old handler and remove the route entry |
| `migration/strangle-unfinished` | error | tripwire: a route is declared strangled but old-implementation traffic still exceeds the retirement threshold in the sunset window — the fig has not strangled it |

Traffic history shorter than the sunset window keeps both tripwires silent — no verdict, no guess.

Plan audit:

| Rule | Severity | Fires when |
| --- | --- | --- |
| `plan/omission` | error | an approved spec change in a mapped area has no generated change under the area it maps to — the intent was never implemented |
| `plan/unapproved-addition` | error | a generated change in a mapped area has no spec basis — scope drift, an invented feature, or hallucinated cleanup |
| `plan/unmapped-spec` | warning | a changed spec file matches no map entry — the coverage map does not cover this intent |
| `plan/unmapped-code` | warning | a changed code file matches no map entry and no allowed-unmapped prefix |

Migration linter:

| Rule | Severity | Fires when |
| --- | --- | --- |
| `migration/naive-backfill` | error | a `Sql` backfill `UPDATE` carries no `WHERE` — the batchless rewrite escalates locks, floods the WAL, and lags replication |
| `migration/destructive-first` | error | a migration drops an object and then references it (or re-adds it), or a later migration re-adds a dropped column — expand before contract |
| `migration/contract-without-gate` | error | a `DropColumn`/`DropTable`/tightening `AlterColumn` removes an emitted object with no gates ledger or no pass for it |
| `migration/removal-without-window` | error | a pass leans on a missing or inverted window — no `expandedAt`/`coldAt`, or `coldAt` before `expandedAt`; a removal needs the measured window, not a date |
| `migration/removal-before-cold` | error | the drop runs before the pass measured the old column cold — the drop is deliberately late, after the window closed |
| `migration/drop-not-isolated` | warning | a contract migration mixes expand/migrate statements — one migration, one revert |

DORA extractor:

| Rule | Severity | Fires when |
| --- | --- | --- |
| `dora/dangling-join` | error | a deployment ships a PR that is not in the changes — the deployment→change join dangles |
| `dora/temporal-join-violation` | error | a change merged after the deployment that claims to ship it — a cause cannot land after its effect |
| `dora/unbuilt-deployment` | error | a deployment's target sha was never observed by a successful CI run — the release is unbuilt |
| `dora/re-run-mislabeled` | error | two deployments of the same environment at the same target sha — the later row is a re-run mislabeled as a new deployment; flagged and excluded from every count |
| `dora/unknown-failure-mode` | error | a failure's restore mode is outside the taxonomy (revert, rollback, hotfix) — an unclassifiable restore is not a restore yet |
| `dora/unevidenced-failure` | error | a failure's mode evidence does not check out — the revert/hotfix PR does not resolve inside the incident window, or the rollback does not target an earlier deployment of a different sha |
| `dora/dangling-failure-join` | error | a failure names a deployment that does not exist |
| `dora/restore-before-detection` | error | a failure was restored before it was detected — service cannot return before it left |
| `dora/change-without-build` | warning | a merged change's merge sha was never observed by a CI run — the change is unverified by the pipeline |
| `dora/unshipped-change` | warning | a merged change is shipped by no deployment — it never reached an environment in this export |

Reviewer calibration harness:

| Rule | Severity | Fires when |
| --- | --- | --- |
| `calib/seed-missed` | error | a seeded defect was approved cleanly — the expected decision was not taken; cites the reviewer, role, queue, decision latency, and edit count |
| `calib/catch-rate-below` | error | a role's catch rate on seeded defects sits below the calibration floor (default 80%) |
| `calib/rubber-stamp-queue` | error | a queue approves at or above the rubber-stamp rate (default 99.8%) with a median decision latency under the floor (default 1.0s) across enough reviews (default 10) — this is not review, it is a broken loop |
| `calib/sub-second-approval` | warning | a queue's median decision latency is under a second — approvals this fast are clicks, not examinations; watch it before it becomes a broken loop |
| `calib/stale-queue` | warning | a queue has no seeded-defect decision within the staleness window (default 30 days) of the export's today — a queue that is never measured cannot be said to work |

Bias probe pack:

| Rule | Severity | Fires when |
| --- | --- | --- |
| `bias/pair-inconsistent` | error | a counterfactual pair's two legs record different scores for the pair's dimension — the marker (the only thing that changed) moved the score; cites the pair and both legs with `file:line` |
| `bias/consistency-below-floor` | error | the deterministic consistency rate sits under the declared floor — the marker changes the outcome at the suite level |
| `bias/consistency-regression` | error | the consistency rate regressed against the committed baseline — cites the pairs that moved, never just the aggregate |
| `bias/consistency-improved` | warning | the consistency rate improved — re-baseline deliberately with `--update-baseline` |
| `bias/threshold-breach` | error | a declared metric's overall aggregate sits below its threshold |
| `bias/baseline-regression` | error | a metric regressed against the committed baseline — cites the cases that moved |
| `bias/cohort-threshold-breach` | error | a metric falls below its threshold for one cohort slice — the slice, not the aggregate, is where bias hides |
| `bias/cohort-baseline-regression` | error | a cohort slice regressed against its baseline — cites the cases that moved |
| `bias/cohort-missing-from-run` | warning | a cohort rode the metric in the committed baseline but no case of this run does — a vanished cohort is a change, never silence |
| `bias/refusal-asymmetry` | error | the refusal-rate delta across cohorts exceeds the ceiling — should-succeed cases must succeed for every cohort alike |
| `bias/refusal-missed` | error | a must-refuse case was answered — fails regardless of cohort |
| `bias/citation-imbalance` | warning | a source cohort's citation share sits outside the declared band — the answers lean on one source cohort; advisory by design |
| `bias/corpus-share-undeclared` | error | a citation references a source cohort `corpusShares` does not declare |
| `bias/no-citation-evidence` | warning | the run carries no citations — citation balance cannot be measured |
| `bias/no-human-calibration` | error | no case carries a human label — judge calibration is mandatory for a bias probe |
| `bias/pair-result-missing` | error | a pair has no recorded result for one of its legs — the pair cannot be measured |
| `bias/pair-dimension-unscored` | warning | a leg's result does not score the pair dimension — excluded from the rate, never guessed |
| `bias/metric-missing-score` | warning | a case riding a metric has no score for it — excluded from the aggregate, never guessed |
| `bias/metric-without-cases` | warning | a declared metric no case rides — the population is declared by cases, never guessed |
| `bias/missing-result` | error | a case has no result in the run — a dropped case is a change, never silence |
| `bias/duplicate-result` | error | the run records a case twice — the rates would double-count it |
| `bias/unknown-result` | error | the run reports an id neither the labeled set nor the pair pack declares |
| `bias/unknown-score` | error | a result carries a score for a key neither the declared metrics nor any pair dimension declares |
| `bias/case-metric-undeclared` | error | a case rides a metric the config does not declare |
| `bias/no-baseline` | warning | no baseline is committed — record one with `--update-baseline` |
| `bias/baseline-metric-missing` | warning | the committed baseline has no entry for a declared metric |

## Honest evaluation status

- **Syntax-level analysis.** The scanner uses Roslyn (`ParseText`) only — no `MSBuildWorkspace`, no compilation, no semantic binding. Usage resolution is by simple type name; when a name is declared in multiple projects the analysis skips it rather than guessing (an "ambiguous" count is in `context-map.json`).
- **No restore required.** Target solutions are never built, so the tool runs against codebases whose dependencies don't even compile on the scan machine. Attribute usages match by attribute name with a fallback suffix.
- **Deterministic output.** Ordinal sorting everywhere, relative paths, no timestamps — identical input produces byte-identical artifacts.
- **Honest findings.** Errors mean a configured boundary rule is violated; warnings are judgment calls for review. Nothing is silently filtered.
- **`tenant-probes` is EF-shaped and convention-checked.** The model scan is syntax-level (no compilation): a DbContext is a class deriving `DbContext`, an entity is a `DbSet<T>` property, and entity namespaces resolve by simple name (ambiguous or missing declarations are loud errors, never guesses). The probes exercise your conventions against a real SQLite in-memory database — they prove the mechanism exists, not that every query in your app is filtered. Pool-checkout checks stand in for `DbContext` pooling with fresh contexts over identical options; if your context pool resets tenant state via `IDbContextFactory`, wire that into the harness. Raw-SQL RLS probes (PostgreSQL `current_setting`-based) are a deliberate non-goal of this version.
- **`events` proves tolerance on replayed cases, not production behavior.** The convention scan is syntax-level: the consumer and outbox resolve by simple class/record name (missing or ambiguous declarations are loud errors). The probes replay deterministic envelopes — ids `case-N`, `skew-N`, `ob-N` — against freshly constructed consumers; they prove the tolerance mechanisms (dedup, dead-lettering, skew handling, outbox idempotency) exist and work on the declared cases, not that every production path is safe. Poison payloads are a fixed corpus (malformed JSON, a non-object message, a type-violating envelope); extend the corpus by adding cases, not by editing generated files.

- **`cockpit` reasons about claims, not proof of traffic.** The pivot and handler scans are syntax-level: wiring evidence is a string-literal route id (dynamic ids are skipped, never guessed), handler resolution is by simple class/record name (missing or ambiguous declarations are loud errors). The shadow-diff suite exercises the declared `old`/`new` handlers under the convention `string <Method>(string input)` with JSON in and JSON out; it proves the two implementations agree on the declared cases within the declared tolerance — it does not observe production traffic. The `traffic` history is data you attest to in the route table; the tripwires check it for consistency, not provenance.

- **`plan-audit` reconciles coverage, not semantics.** Machine matches are area-level: a changed spec file and a changed code file in mapped areas reconcile each other — the audit proves every changed file on both sides is accounted for, not that a change is the right implementation of a requirement. Line anchors cite the first added line (deletion-only changes anchor the hunk start). Semantic matches are externally produced (an LLM pass is the expected producer), accepted only when both anchors are lines the diffs actually change, and always labeled `semantic` — the tool never invents a reconciliation. The audit is advisory by design: `--strict` makes error findings a non-zero exit where a team wants a gate.

- **`migration-lint` reads EF Core migration files, not a compiled model.** The parser is syntax-level: EF Core file names must be `<14-digit-timestamp>_<Name>.cs`, duplicate timestamps and missing `Up` methods are loud errors, and **unknown `migrationBuilder` calls fail loudly rather than being guessed** (known set: `CreateTable`, `DropTable`, `RenameTable`, `AddColumn`, `DropColumn`, `AlterColumn`, `RenameColumn`, `CreateIndex`, `DropIndex`, `AddForeignKey`, `DropForeignKey`, `Sql` — e.g. `EnsureSchema` is a loud error until added to the known set). Only the `Up` method's statements are analyzed; the analysis proves the committed history obeys the expand-migrate-contract ordering and the ledger's measured windows — it does not observe the production database or traffic. `expandedAt`/`coldAt` are dates you attest to in the ledger; the linter checks the history against them, not their provenance.

- **`dora` reads a recorded export; the joins are verified, not observed.** The extractor never calls an API live — wiring a live system means producing an export in the documented format. It checks the export's internal consistency (deployment → change → build, failure → restore evidence) against the recorded data; it does not observe production, and the failure rows are recorded from incident review, which is exactly why their taxonomy evidence is checked rather than trusted. The series carries the `dora-definitions-v1` version — trends, not scores: no benchmark labels, ever.

- **`eval` judges recorded runs; it never calls a model.** The harness reads a committed `results.json` your system (or your judge pipeline) produced in the documented format, aggregates per-stage metrics over the cases that have scores (unscored cases are excluded and flagged, never guessed), and gates against thresholds and the committed baseline. Judge agreement is measured against the human-labeled sample only — abstains never count as agreement, and disagreements are named for calibration, not treated as failures. Aggregates are means rounded to 4 decimal places (AwayFromZero); the gate compares the rounded value. The harness proves the run's recorded outcomes hold and are accounted for; it does not prove the recording itself was honest.

- **`review-calib` measures recorded decisions, not reviewers.** The harness reads a committed export of review decisions — it never calls an API live, and it never ranks people: catch rates, approval telemetry, and judge agreement are calibration instruments for the queues and the autonomy ladder, not a leaderboard. The seeded defects are planted in the export; the rates measure the recorded window only. The judge column is a recorded verdict the human decisions are compared against — disagreement is named for calibration, never treated as failure. The harness proves the recorded window's evidence holds; it does not observe reviews live, and it does not prove the export itself was honest.

- **`bias-probe` measures recorded score pairs, not the model's soul.** The probe reads a committed results file the way the eval harness does — it never calls a model. Consistency is exact equality of the two recorded dimension scores (the recorded scores are the measurement), the rate is deterministic, and every gate compares rounded aggregates against declared floors and the committed baseline — the probe proves the recorded run's bias properties hold and are accounted for; it does not prove the recording itself was honest, and a judge that scores the marker differently without changing the outcome is invisible to it. Citation balance is advisory by design: the drift is published so it can be watched, not failed — only an undeclared source cohort errors. Human calibration is mandatory because the whole instrument reduces to the judge's scores without it.

## Demo

CI demonstrates both analysis commands against two public example codebases — [ErpLight](https://github.com/DevelApp-ai/ErpLight) (plugin-module ERP) and [RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory) (runtime pluggable class factory) — and uploads the generated maps, findings, and ledgers as workflow artifacts.

The tenant-probe demo is in-repo: [`demo/MultitenantApp`](demo/README.md), a small EF Core app with a per-tenant query filter and a fail-closed `SaveChanges` guard, plus two deliberately regressed variants (query filter removed; save guard removed). CI generates the probe suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** — the #58 done-when proof: isolation regressions fail loudly, by construction.

The cockpit demo is also in-repo: [`demo/StranglerApp`](demo/README.md), a small strangler-fig app whose `shipping.quote` route is strangled end to end — promoted at the routing pivot, shadow-diffed green against the retired legacy handler, and retired below the traffic threshold, so the sunset tripwire fires (the #56 done-when: one route strangled with the tripwire armed and firing). CI also runs three deliberately broken variants: a drifted new implementation (the shadow suite fails with `SHADOW DRIFT`), an unwired declared route (`migration/unwired-route` error), and a strangled route whose traffic contradicts the claim (`migration/strangle-unfinished` error).

The events demo is in-repo too: [`demo/EventApp`](demo/README.md), a small eventing app with a compliant `OrderEventsConsumer` (dedup, dead letters, old-schema tolerance) and `Outbox`, two event contracts — `order.placed` v2 with a v1 skew replay, and `invoice.issued` v1 — plus two deliberately regressed variants: a naive consumer (no dedup, crashes on poison, refuses old versions) and a fire-and-forget outbox (publishes vanish, dispatches leave no bookkeeping). CI generates the suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** with `EventConformance.ConsumerProbes` / `EventConformance.OutboxProbes` — the #60 done-when: seeded faults visibly fail a naive consumer and pass a compliant one.

The plan-audit demo is in-repo too: [`demo/PlanAudit`](demo/README.md), a small QuoteTree estate with two spec areas (quote, billing) mapped to their code areas and four committed diff pairs: a conformant control, an omission (the billing spec gains "invoices are exportable as PDF" that no generated change implements), an unapproved addition (a surprise fee appears in `InvoiceService`), and a cross-area pair (the audit-trail requirement implemented in a new file outside the map's coverage) that only a labeled semantic match reconciles. CI runs all four — the control is silent, each seeded discrepancy fires with its line-anchored citation, and regeneration is byte-identical — the #64 done-when: the auditor bites on omission and unapproved addition and stays silent on a conformant pair, deterministically.

The eval demo is in-repo too: [`demo/EvalApp`](demo/README.md), a docs-assistant suite with two stages (retrieval scored by `recallAt3`, generation scored by `faithfulness` and `refusalCorrectness`), nine cases — seven human-labeled, one of which the judge disagrees on so the agreement board shows real calibration (6/7 = 0.8571, disagreements named) — a committed baseline, and two deliberately broken recorded runs: a regression (r3 drops out of the top-3, g2's summary drifts — both metrics fall below baseline *and* threshold, `--strict` exits 1 with `eval/baseline-regression` citing the moved cases) and an undeclared case (r99 appears in the run, `eval/unknown-result`). CI runs all three — the control is clean with its calibration visible, the committed baseline is byte-identical to what `--update-baseline` writes, and each seeded failure bites — the #66 done-when: the seeded suite runs in CI, the seeded regression is caught with per-case evidence, judge agreement is reported against the human-labeled sample, and everything reproduces from a clean checkout.

The migration-lint demo is in-repo too: [`demo/MigrationApp`](demo/README.md), a five-migration EF Core estate that walks the expand → backfill → contract sequence by the book — `Orders.ShippingAddress` is replaced by `DeliveryAddress` with a batched, gated, measured window (expanded 2026-03-01, cold 2026-05-25, dropped 2026-06-01, isolated in its own migration) — plus five deliberately broken variants: a naive unbatched backfill (`migration/naive-backfill`), a drop-then-archive that references the dropped column (`migration/destructive-first` + `migration/drop-not-isolated`), an ungated removal of `Orders.LegacyTotal` (`migration/contract-without-gate`), an unmeasured window (no `coldAt` — `migration/removal-without-window`), and a rushed window whose drop runs before the column measured cold (`migration/removal-before-cold`). CI runs the control clean and byte-identical on regeneration, then asserts each variant bites under `--strict` with its cited evidence — the #67 done-when: the control stays silent, each seeded violation is caught by construction, deterministically.

The DORA demo is in-repo too: [`demo/DoraApp`](demo/README.md), a recorded export of a **real** public repository — DevelApp-ai/RuntimePluggableClassFactory's October 2025 release train (v2.0.2 → v2.1.0, 12 releases in 10 days) — with 13 merged changes, 46 CI runs, and the one failure visible in the public history: v2.0.7 broke the GitHub Pages deploy, hotfixed by PR #18 in v2.0.8 (mode `hotfix`, MTTR 2.8 h, evidence verified inside the incident window). v2.0.9 is a direct commit with no PR — an empty `prs` list is legal because the extractor verified it empty, not because it skipped it. Five deliberately broken variants from `demo/broken/` bite under `--strict` via `--events`: a re-run mislabeled as a new deployment (`dora/re-run-mislabeled`, excluded from every count — the #68 done-when), a dangling PR join, an unbuilt release, an unknown restore mode, and unevidenced hotfix evidence. CI runs the control clean with the joins table verified and the committed definitions file byte-identical on regeneration.

The review-calib demo is in-repo too: [`demo/ReviewCalibApp`](demo/README.md), a recorded export of review decisions for a synthetic `example/checkout` repository — 20 reviews across two queues (`checkout`, `infra`) and two roles (`senior`, `junior`), eight seeded defects (all caught in the control), and a recorded judge that disagrees on `rv-005` so the agreement board shows real calibration (0.8 on the judged sample, disagreements named). Three deliberately broken variants from `demo/broken/` bite under `--strict` via `--events`: a seeded defect approved cleanly (`calib/seed-missed` — the #77 done-when, the one miss the instrument exists to catch), a role below the calibration floor (`calib/catch-rate-below` — junior catches 3 of 4, 75.0% against the 80% floor), and a rubber-stamping queue (`calib/rubber-stamp-queue` — checkout approves 12 of 12 at a 0.5 s median, plus `calib/stale-queue` on its unmeasured window). CI runs the control clean with every seed caught and the board byte-identical on regeneration.

The bias-probe demo is in-repo too: [`demo/BiasProbeApp`](demo/README.md), a docs-assistant probe pack on the #66 estate — ten counterfactual pairs (Anna vs Ahmed across tone, hedging, length, and benefitOfTheDoubt), sixteen cases sliced over two region cohorts with a human-labeled sample the judge disagrees on one of (agreement 0.6667, `g3` named), should-succeed and must-refuse cases, retrieval cases with citations matching the declared 0.75/0.25 corpus shares, and a committed baseline. CI runs the control clean (consistency 1.0, byte-identical on regeneration, baseline exactly what `--update-baseline` writes), then asserts five deliberately broken variants from `demo/broken/` bite under `--strict` via `--results`: a marker flip that moves the tone score (`bias/pair-inconsistent` + `bias/consistency-regression` citing `p3` — the #78 done-when, the seeded inconsistency caught with the pair and dimension named), a cohort regression the overall hides (`bias/baseline-regression` + `bias/cohort-baseline-regression` citing `g5` — region-b 0.8167 while the overall 0.8833 sits above the 0.8 threshold), a refusal gap (`bias/refusal-asymmetry` — region-b refuses 2 of 5 against region-a's 0), an answered must-refuse case (`bias/refusal-missed`), and citation skew (advisory: 2 warnings, exit 0 by design).

## Roadmap

See [docs/roadmap.md](docs/roadmap.md). Implemented: #54 (context mapper / boundary doctor), #55 (seam ledger / evidence linter), #58 (tenant-isolation probe generator), #56 (strangler-fig migration cockpit), #60 (event contract conformance harness), #64 (plan-conformance auditor), #66 (LLM evaluation harness runner and baseline gate), #67 (expand-migrate-contract migration linter), #68 (DORA four-key extractor), #77 (reviewer calibration harness).

## License

MIT — see [LICENSE](LICENSE).

