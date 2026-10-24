# Demo estates

Small in-repo demonstration estates for the `sfg` commands. Each one exists so CI can prove
its command **bites**: the good app passes, and a deliberately regressed variant fails with
the cited evidence. Generated output (`probes-*`, `cockpit-*`, `events-*`, `planaudit-*`,
`eval-*`) is never committed — CI regenerates it and uploads the manifest/board as the
workflow artifact.

## [EventApp](EventApp) — `sfg events` (#60)

A small eventing app: a compliant `OrderEventsConsumer` (dedup, dead letters, old-schema
tolerance) and an `Outbox`, two event contracts — `order.placed` v2 with a v1 skew replay,
and `invoice.issued` v1. CI generates the conformance suite, runs it green against the good
app, then swaps in a naive consumer and a fire-and-forget outbox from
[`broken/`](broken) and asserts the suite **fails** — the #60 done-when.

## [MultitenantApp](MultitenantApp) — `sfg tenant-probes` (#58)

A small EF Core app with a per-tenant query filter and a fail-closed `SaveChanges` guard.
CI generates the probe suite, runs it green, then swaps in the query-filter-removed and
save-guard-removed variants from [`broken/`](broken) and asserts the probes **fail** —
the #58 done-when.

## [StranglerApp](StranglerApp) — `sfg cockpit` (#56)

A small strangler-fig app whose `shipping.quote` route is strangled end to end — promoted
at the routing pivot, shadow-diffed green, and retired below the traffic threshold, so the
sunset tripwire fires. CI also runs a drifted implementation, an unwired route, and a
strangled route whose traffic contradicts the claim — the #56 done-when.

## [PlanAudit](PlanAudit) — `sfg plan-audit` (#64)

A small QuoteTree estate with two spec areas (quote, billing) mapped to their code areas,
and four committed diff pairs: a conformant control, an **omission** (an approved spec
change no generated change implements), an **unapproved addition** (a generated change with
no spec basis), and a cross-area pair only a labeled semantic match reconciles. CI runs all
four — the control is silent, each seeded discrepancy fires with its line-anchored
citation, and regeneration is byte-identical — the #64 done-when.

## [EvalApp](EvalApp) — `sfg eval` (#66)

A docs-assistant eval suite with two stages — retrieval scored by `recallAt3`, generation
scored by `faithfulness` and `refusalCorrectness` — nine cases (seven human-labeled), a
committed baseline, and a conformant recorded run whose judge disagrees with the human on
`g3`, so the agreement board shows real calibration (6/7 = 0.8571). CI runs the control
clean, regenerates the baseline byte-identically, then judges two deliberately broken
recorded runs from [`broken/`](broken): a **regression** (r3 drops out of the top-3 and
g2's summary drifts — both metrics fall below baseline *and* threshold; `--strict` exits 1
and `eval/baseline-regression` cites the moved cases) and an **undeclared case** (r99
appears in the run; `eval/unknown-result`). The harness judges recorded runs — it never
calls a model — the #66 done-when.

## [MigrationApp](MigrationApp) — `sfg migration-lint` (#67)

A five-migration EF Core estate that walks the expand → backfill → contract sequence by
the book: `Orders.ShippingAddress` is replaced by `DeliveryAddress` with a batched backfill,
a recorded gate pass (expanded 2026-03-01, cold 2026-05-25, consumers rewired), and an
isolated drop on 2026-06-01 — deliberately late. CI runs the control clean, regenerates the
board byte-identically, then asserts five deliberately broken variants from [`broken/`](broken)
bite under `--strict`: a **naive unbatched backfill** (`migration/naive-backfill`), a
**drop-then-archive** that references the dropped column (`migration/destructive-first` +
`migration/drop-not-isolated`), an **ungated removal** of `Orders.LegacyTotal`
(`migration/contract-without-gate`), an **unmeasured window** (`--gates` with no `coldAt` —
`migration/removal-without-window`), and a **rushed window** whose drop runs before the
column measured cold (`migration/removal-before-cold`). The control stays silent and each
seeded violation is caught by construction — the #67 done-when.
