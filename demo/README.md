# Demo estates

Small in-repo demonstration estates for the `sfg` commands. Each one exists so CI can prove
its command **bites**: the good app passes, and a deliberately regressed variant fails with
the cited evidence. Generated output (`probes-*`, `cockpit-*`, `events-*`, `planaudit-*`) is
never committed — CI regenerates it and uploads the manifest/board as the workflow artifact.

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
