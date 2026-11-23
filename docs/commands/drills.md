# sfg drills

> Book issue #81 Resilience drill evidence harness.

Judges a recorded drill export post-hoc against the committed scenario catalog: every alert fired inside its escalation clock, every restore met its RTO, the RPO data-loss window held, the expected telemetry signals are on the record, participants and component versions are named, and every scenario is drilled fresh — a DR plan on paper and a rehearsed one must not look alike. Judges recorded evidence only; it never injects failures (the #68 discipline). Staleness is measured against the export's recorded asOf instant, never the wall clock, so the analysis is deterministic. Advisory by default; `--strict` exits non-zero on errors.

## Usage

```bash
# resilience drill evidence harness: clocks, RTO/RPO, signals, freshness
sfg drills /path/to/app --out drills
# drills also accepts --config drills.json (it defaults to
#   <root>/drills.json) and --evidence to judge a different recorded
#   drill export (e.g. a seeded broken one); --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`drills` writes the drill evidence board:

- `drills.md` — the human board: scenario freshness (last drilled, age against the recorded asOf instant, per-scenario window, status — a never-drilled scenario is visible, not silent), clocks (alert vs clock, restore vs RTO, data-loss window vs RPO, verdict per drill record), expected-signal coverage (missing signals named), participation (participants and component versions on the record), and findings sorted errors-first with `file:line` citations. A clean run renders the explicit `✅ Clean run` line. The board carries its honest status: `sfg drills` judges recorded drill evidence — it never injects failures.
- `drills.json` — the machine summary: generator, drill id, asOf, per-scenario freshness, per-record clock readings, missing signals, participation, findings with rule/severity/message/evidence, error and warning counts.

## Inputs

## Drill evidence harness (`drills.json`, the drills harness's required input)

```json
{
  "name": "pipeline-drills",
  "judge": "drill-evidence-judge-v1",
  "stalenessDays": 180,
  "scenarios": "scenarios.json",
  "evidence": "drill-evidence.json"
}
```

`stalenessDays` is the freshness window (default 180; a scenario can override it with `maxStalenessDays`). Paths resolve relative to the config's directory.

`scenarios.json` is the committed failure-injection catalog — the drill population is declared by scenarios, never guessed:

```json
{ "scenarios": [
  { "id": "d1", "title": "pdf build restore",
    "failure": "the book PDF workflow fails mid-build during a release rehearsal",
    "expectedSignals": [ "ci/build-failed", "pager/alert-routed" ],
    "clocks": { "alertSeconds": 300, "rtoSeconds": 3600, "rpoSeconds": 900 },
    "maxStalenessDays": 180 }
] }
```

`drill-evidence.json` is the recorded export the harness judges — it never runs the drill:

```json
{ "drill": "pipeline-drill-2026-10",
  "asOf": "2026-10-15T00:00:00Z",
  "records": [
    { "scenario": "d1", "startedAt": "2026-09-20T08:00:00Z",
      "alertedAt": "2026-09-20T08:03:00Z", "restoredAt": "2026-09-20T08:45:00Z",
      "lastRecoverableAt": "2026-09-20T08:40:00Z",
      "signals": [ { "key": "ci/build-failed", "at": "2026-09-20T08:02:30Z" } ],
      "participants": [ "release-on-call" ],
      "versions": { "ci": "2026.10", "runbook": "rb-114" } }
  ] }
```

`asOf` is the recorded reference instant staleness is measured against — never the wall clock, so the analysis is deterministic. Clock budgets are inclusive at the boundary: an alert exactly at its clock, a restore exactly at its RTO, or a loss window exactly at its RPO is met.

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `drill/late-alert` | error | the alert fired after the scenario's alert clock — the escalation clock is cited, with the drill record |
| `drill/alert-never-fired` | error | the drill record shows no alert — the escalation clock never started |
| `drill/restore-over-rto` | error | the restore exceeded the scenario's RTO — cited against the injection instant |
| `drill/restore-never-happened` | error | the drill record shows no restore — the RTO was not met |
| `drill/rpo-breached` | error | the restore landed more than an RPO after the last recoverable data — the data-loss window is judged, not assumed |
| `drill/rpo-unrecorded` | warning | the record restored but names no last-recoverable data timestamp — the RPO is unverifiable; advisory, never silent |
| `drill/signal-missing` | error | an expected telemetry signal is absent from the drill record |
| `drill/unknown-signal` | warning | the record carries a signal the catalog does not expect — the record disagrees with the catalog; watch it |
| `drill/participants-unrecorded` | error | the drill names neither participants nor component versions — evidence that evaporates names no one |
| `drill/never-drilled` | error | a catalog scenario has no drill record — a DR plan on paper and a rehearsed one must not look alike |
| `drill/stale-scenario` | warning | the latest drill is older than the scenario's freshness window (or the config default) — advisory, the drift is published |
| `drill/evidence-undeclared` | error | a drill record targets a scenario the catalog never declared — the population is declared by scenarios, never guessed |
| `drill/duplicate-drill` | error | one drill export records the same scenario twice — one drill, one record |

## Honest status

- **`drills` judges recorded evidence; it never injects failures.** The harness reads a committed drill export — it does not rehearse anything, and it does not prove the export itself was honest. Clock budgets (alert, RTO, RPO) are inclusive at the boundary and judged from the recorded timestamps; freshness is measured against the export's recorded `asOf` instant, never the wall clock, so the analysis is deterministic. Staleness and stray signals are advisory by design: the drift is published so it can be watched, not failed — a never-drilled scenario, a late alert, a missed RTO or RPO, and unrecorded participants error. The harness proves the recorded window's drills hold and are accounted for; it does not observe the drill live.

## In-repo demo

The drills demo is in-repo too: [`demo/DrillApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a resilience drill estate on the book's own pipeline as the rehearsal target — four scenarios (PDF build restore, kill-switch re-entry, database failover, dependency registry outage), each with declared clocks (alert, RTO, RPO) and expected telemetry signals, and a conformant recorded export (drill `pipeline-drill-2026-10`, asOf 2026-10-15). CI runs the control clean (all clocks met, all signals recorded, every scenario fresh, byte-identical on regeneration), then asserts six deliberately broken variants from `demo/broken/` bite under `--strict` via `--evidence`: a late alert (`drill/late-alert` — d3 alerts 120s over its 60s clock, the #81 done-when), an over-RTO restore (`drill/restore-over-rto` — d1 restores 4200s against a 3600s RTO), a breached RPO (`drill/rpo-breached` — d2 loses 900s against a 600s RPO), a never-drilled scenario (`drill/never-drilled` — d4 vanishes from the export: a paper plan caught), a missing expected signal (`drill/signal-missing`), and a stale scenario (`drill/stale-scenario` — 283 days old, advisory warning, exit 0 by design).
