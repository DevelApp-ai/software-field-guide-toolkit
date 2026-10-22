# Demos

Two in-repo applications demonstrate the tools' done-when proofs — both are
deliberately small, both are exercised in CI, and both carry deliberately
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
