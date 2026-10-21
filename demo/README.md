# Demo: multitenant isolation probes (#58)

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
