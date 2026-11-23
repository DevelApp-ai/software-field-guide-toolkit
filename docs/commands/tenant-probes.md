# sfg tenant-probes

> Book issue #58 Multitenant isolation probe generator.

Scans an EF Core model (DbContext, `DbSet<T>` entities) and generates a compilable xunit probe suite: cross-tenant read probes, fail-closed save guards, pool-checkout session-leakage checks, and disabled-state fail-closed probes — one `[Fact]` per entity, all citing the scanned file:line inventory. Runs in CI so isolation regresses loudly.

## Usage

```bash
# tenant-isolation probe suite: compilable xunit project
sfg tenant-probes /path/to/multitenant/app --out probes
# tenant-probes also accepts --config tenant-probes.json (see below)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`tenant-probes` writes a compilable xunit project (run it with `dotnet test`):

- `TenantProbes.csproj` — references your project and the EF Core provider; regenerating is safe, the output is byte-identical for the same scan.
- `ProbeHarness.cs` — owns an in-memory SQLite database, seeds one row per probe tenant per entity, and checks out additional contexts over the same options (the pool-checkout stand-in).
- `CrossTenantReadProbes.cs` — per entity: tenant B's query must return exactly tenant B's rows.
- `SaveGuardProbes.cs` — per entity: saving an entity stamped with a foreign tenant id must throw.
- `PoolCheckoutProbes.cs` — per entity: a fresh context over the same options must not inherit the previous tenant's session state.
- `DisabledStateFailClosedProbes.cs` — per entity: with the tenant context absent, reads return nothing and writes throw (never a fallback to all tenants).
- `manifest.json` — the versioned probe inventory: DbContext (with file:line), every probed entity (name, namespace, DbSet, file:line), config echo.
- `README.md` — what each probe class bites on, and the probed-entity table.

## Inputs

## Configuration (`tenant-probes.json`, all keys optional)

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

## Conventions the probe target must satisfy

- the DbContext constructor accepts `DbContextOptions`,
- the DbContext exposes a **settable** ambient-tenant property (`CurrentTenantId` by default) — the session state the probes manipulate,
- every probed entity has a settable tenant property (`TenantId` by default) and a parameterless constructor.

If a convention is missing the generated suite fails to compile — deliberately loud, not a silent skip.

## Honest status

- **`tenant-probes` is EF-shaped and convention-checked.** The model scan is syntax-level (no compilation): a DbContext is a class deriving `DbContext`, an entity is a `DbSet<T>` property, and entity namespaces resolve by simple name (ambiguous or missing declarations are loud errors, never guesses). The probes exercise your conventions against a real SQLite in-memory database — they prove the mechanism exists, not that every query in your app is filtered. Pool-checkout checks stand in for `DbContext` pooling with fresh contexts over identical options; if your context pool resets tenant state via `IDbContextFactory`, wire that into the harness. Raw-SQL RLS probes (PostgreSQL `current_setting`-based) are a deliberate non-goal of this version.

## In-repo demo

The tenant-probe demo is in-repo: [`demo/MultitenantApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a small EF Core app with a per-tenant query filter and a fail-closed `SaveChanges` guard, plus two deliberately regressed variants (query filter removed; save guard removed). CI generates the probe suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** — the #58 done-when proof: isolation regressions fail loudly, by construction.
