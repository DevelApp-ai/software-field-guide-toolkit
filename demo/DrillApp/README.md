# DrillApp — `sfg drills` (#81)

A resilience drill estate with the book's own pipeline as the rehearsal target
(self-referential: PDF build restore, kill-switch re-entry) plus anonymized
platform runbooks (database failover, dependency registry outage). The harness
judges the recorded drill export post-hoc — it never injects failures (the
#68 discipline: recorded evidence, not live systems):

| Asset | What it holds |
| --- | --- |
| `drills.json` | the suite contract: staleness window 180 days, scenario catalog and evidence file names |
| `scenarios.json` | four scenarios (`d1`–`d4`), each with the failure it injects, the telemetry signals a drill must record, and its clocks: `alertSeconds`, `rtoSeconds`, `rpoSeconds` |
| `drill-evidence.json` | the conformant recorded export: drill `pipeline-drill-2026-10`, asOf 2026-10-15, one record per scenario — every alert inside its clock, every restore within RTO, every RPO held, all expected signals recorded, participants and versions named |

Staleness is measured against the export's recorded `asOf` instant — never the
wall clock — so the analysis is deterministic. A drill that happened once and
left no record is indistinguishable from a rehearsal that never happened: that
is the problem the harness exists to catch.

Run it:

```bash
sfg drills demo/DrillApp --out drill-good
```

The control is silent (`✅ Clean run`, 0 findings) and byte-identical on
regeneration. The broken variants live in [`../broken`](../broken) and CI
judges them via `--evidence`:

| Variant | How CI seeds it | Rule that bites |
| --- | --- | --- |
| `drill-evidence.LateAlert.json` | `d3` alerts 120s after injection (clock 60s) | `drill/late-alert` — cites the scenario clock and the drill record; `--strict` exits 1 |
| `drill-evidence.OverRto.json` | `d1` restores 4200s after injection (RTO 3600s) | `drill/restore-over-rto` — the restore cites the record against the RTO clock |
| `drill-evidence.RpoBreached.json` | `d2` restores 900s after its last recoverable data (RPO 600s) | `drill/rpo-breached` — the data-loss window is judged, not assumed |
| `drill-evidence.NeverDrilled.json` | `d4` has no record in the export | `drill/never-drilled` — a DR plan on paper and a rehearsed one must not look alike |
| `drill-evidence.SignalMissing.json` | `d4` never records `build/registry-unreachable` | `drill/signal-missing` — the expected telemetry the drill failed to capture |
| `drill-evidence.StaleScenario.json` | `d2` last drilled 283 days before asOf (window 180) | `drill/stale-scenario` (warning) — advisory by design: the freshness drift is published, `--strict` still exits 0 because nothing errored |

Every bite cites `file:line` evidence — the scenario's declared clock and the
drill record that violated it. The control stays silent; each seeded violation
is caught exactly once — the #81 done-when.
