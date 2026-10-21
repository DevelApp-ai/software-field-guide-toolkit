# Software Field Guide Toolkit

Companion tools for the book *The Software Field Guide* (DevelApp-ai/software-field-guide). `sfg` extracts architecture **truth from code** — not from what the diagrams claim — and renders editable, evidence-cited artifacts.

Two commands ship today, mapping to book issues [#54](https://github.com/DevelApp-ai/software-field-guide/issues/54) and [#55](https://github.com/DevelApp-ai/software-field-guide/issues/55), plus the tenant-isolation probe generator from [#58](https://github.com/DevelApp-ai/software-field-guide/issues/58):

| Command | Book issue | What it does |
| --- | --- | --- |
| `sfg context-map` | #54 Architecture context mapper and boundary doctor | Scans a .NET solution, builds the context dependency graph, and flags reach-arounds, forbidden directions, shared-kernel sprawl, and orphaned seams — every finding with file:line evidence. Emits an editable drawio map in the book's diagram conventions. |
| `sfg seam-ledger` | #55 Extension-point ledger and seam evidence linter | Inventories every seam (interfaces, plugin contracts, event hooks) with consumer counts, flags one-implementation seams as speculative generality, checks versioning and default implementations, and renders the collapse-review checklist. The evidence bar is a required input, never a guess. |
| `sfg tenant-probes` | #58 Multitenant isolation probe generator | Scans an EF Core model (DbContext, `DbSet<T>` entities) and generates a compilable xunit probe suite: cross-tenant read probes, fail-closed save guards, pool-checkout session-leakage checks, and disabled-state fail-closed probes — one `[Fact]` per entity, all citing the scanned file:line inventory. Runs in CI so isolation regresses loudly. |

The two analysis commands share one syntax-level scan engine, so a seam in the ledger and a contract in the map are the same underlying type. The probe generator is the tool's one output that **compiles against your code** — that is the point: the probes bite, or the isolation is already broken.

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

# all accept:
#   --config sfg.json       analysis configuration (see below)
#   --out DIR               output directory (default sfg-out/<command>)
#   --strict                exit 1 if any error-severity finding (context-map)
#   --evidence evidence.json seam evidence entries (seam-ledger)
# tenant-probes also accepts --config tenant-probes.json (see below)
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

## Honest evaluation status

- **Syntax-level analysis.** The scanner uses Roslyn (`ParseText`) only — no `MSBuildWorkspace`, no compilation, no semantic binding. Usage resolution is by simple type name; when a name is declared in multiple projects the analysis skips it rather than guessing (an "ambiguous" count is in `context-map.json`).
- **No restore required.** Target solutions are never built, so the tool runs against codebases whose dependencies don't even compile on the scan machine. Attribute usages match by attribute name with a fallback suffix.
- **Deterministic output.** Ordinal sorting everywhere, relative paths, no timestamps — identical input produces byte-identical artifacts.
- **Honest findings.** Errors mean a configured boundary rule is violated; warnings are judgment calls for review. Nothing is silently filtered.
- **`tenant-probes` is EF-shaped and convention-checked.** The model scan is syntax-level (no compilation): a DbContext is a class deriving `DbContext`, an entity is a `DbSet<T>` property, and entity namespaces resolve by simple name (ambiguous or missing declarations are loud errors, never guesses). The probes exercise your conventions against a real SQLite in-memory database — they prove the mechanism exists, not that every query in your app is filtered. Pool-checkout checks stand in for `DbContext` pooling with fresh contexts over identical options; if your context pool resets tenant state via `IDbContextFactory`, wire that into the harness. Raw-SQL RLS probes (PostgreSQL `current_setting`-based) are a deliberate non-goal of this version.

## Demo

CI demonstrates both analysis commands against two public example codebases — [ErpLight](https://github.com/DevelApp-ai/ErpLight) (plugin-module ERP) and [RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory) (runtime pluggable class factory) — and uploads the generated maps, findings, and ledgers as workflow artifacts.

The tenant-probe demo is in-repo: [`demo/MultitenantApp`](demo/README.md), a small EF Core app with a per-tenant query filter and a fail-closed `SaveChanges` guard, plus two deliberately regressed variants (query filter removed; save guard removed). CI generates the probe suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** — the #58 done-when proof: isolation regressions fail loudly, by construction.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md). Implemented: #54 (context mapper / boundary doctor), #55 (seam ledger / evidence linter), #58 (tenant-isolation probe generator). Next per the book's build order: #56, #60.

## License

MIT — see [LICENSE](LICENSE).
