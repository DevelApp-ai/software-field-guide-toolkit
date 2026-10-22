# Demos

Three in-repo applications demonstrate the tools' done-when proofs — all are
deliberately small, all are exercised in CI, and all carry deliberately
broken variants that must make the generated suites fail loudly.

## Multitenant isolation probes (#58)

`MultitenantApp` is a small but realistic EF Core application — the probe
target for `sfg tenant-probes`. It carries exactly two isolation mechanisms
the generated suite defends:

1. **Global query filter** per entity (`HasQueryFilter` on `TenantId`),
2. **Fail-closed SaveChanges guard** (foreign-tenant writes throw; with no
   tenant context, nothing is written).

Conventions the target must satisfy (also documented in the README):

- `AppDbContext` has a constructor accepting `DbContextOptions`,
- `AppDbContext` exposes a settable `CurrentTenantId` (the ambient tenant),
- every probed entity has a settable `TenantId` property and a
  parameterless constructor.

`broken/` holds two deliberately regressed variants of the same context,
stored outside the compile scope so they never build together with the real
one:

- `AppDbContext.NoFilter.cs` — query filter removed (reads leak),
- `AppDbContext.NoGuard.cs` — save guard removed (writes leak).

CI copies the app, swaps a broken variant in over `AppDbContext.cs`,
regenerates the probes, and asserts the probe suite FAILS — the done-when
proof for #58: isolation regressions fail loudly, by construction.

## Strangler-fig migration cockpit (#56)

`StranglerApp` is a small strangler-fig application — the target for
`sfg cockpit`. It has a routing pivot (`MigrationPivot`), three wired
routes, and one route strangled end to end:

- `orders.search` — strangling: legacy handler still serves, new
  implementation shadows it against the declared cases within tolerance,
- `orders.get` — todo: legacy only, the fig has not started,
- `shipping.quote` — strangled: promoted at the pivot
  (`pivot.Promote("shipping.quote")`), shadow-diffed green against the
  retired legacy handler, and its old-implementation traffic
  `[4200, 3100, 1400, 210, 90, 0, 0, 0]` has stayed under the retirement
  threshold (100) for the sunset window (3 periods) — so the sunset
  tripwire fires: the #56 done-when, one route strangled with the
  tripwire armed and firing.

Handlers follow the shadow-diff convention: `string Handle(string input)`
with JSON in and JSON out. The legacy implementations round totals to
cents; the new implementations report at mill precision — the same math,
different rendering, absorbed by the declared numeric tolerance
(`total ± 0.01`, `quote ± 0.01`); `servedBy` and `elapsedMs` are ignored.

`broken/` holds three deliberately regressed variants, stored outside the
compile scope so they never build together with the real ones:

- `OrdersSearchService.Drifted.cs` — the new implementation drifted 5%
  above the legacy totals: the generated shadow-diff suite must FAIL with
  `SHADOW DRIFT on route 'orders.search'`,
- `migration.UnwiredRoute.json` — the route table declares a `promo.code`
  route the pivot never wires: `sfg cockpit --strict` must exit non-zero
  with a `migration/unwired-route` error,
- `migration.LyingStrangled.json` — `shipping.quote` is declared strangled
  but its traffic never retired below the threshold: `sfg cockpit` must
  report the `migration/strangle-unfinished` error.

CI runs the good app end to end (cockpit board + tripwire + shadow suite
green), proves the outputs are byte-identical on regeneration, then swaps
each broken variant in and asserts the tool bites.

## Event contract conformance harness (#60)

`EventApp` is a small eventing application — the target for
`sfg events`. It carries a compliant `OrderEventsConsumer` (dedupes by
message id, routes poison to the dead letters, tolerates old-schema
replays) and a compliant `Outbox` (a publish waits in the pending queue;
double dispatch stays idempotent), plus two event contracts:

- `order.placed` — version 2, three case payloads, and a declared
  `previousVersion` 1 with two old-schema skew cases,
- `invoice.issued` — version 1, two case payloads, no skew.

Conventions the target must satisfy (also documented in the README):

- the consumer exposes `Handle(string message)`, `AppliedEffects`
  (applied message ids, deduplicated), and `DeadLetters` (raw
  dead-lettered messages), with a parameterless constructor,
- the outbox (optional) exposes `Publish(string)`,
  `MarkDispatched(string)`, `Pending`, and `Dispatched`.

`broken/` holds two deliberately regressed variants, stored outside the
compile scope so they never build together with the real ones:

- `OrderEventsConsumer.Naive.cs` — no dedup (duplicates apply twice),
  crashes on poison payloads, and refuses old versions outright: the
  generated conformance suite must FAIL with
  `Failed EventConformance.ConsumerProbes`,
- `Outbox.FireAndForget.cs` — publishes vanish and dispatches leave no
  bookkeeping: the suite must FAIL with
  `Failed EventConformance.OutboxProbes`.

CI runs the good app end to end (board + suite green), proves the
outputs are byte-identical on regeneration, then swaps each broken
variant in and asserts the suite bites — the #60 done-when: seeded
faults visibly fail a naive consumer and pass a compliant one.
