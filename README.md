# Software Field Guide Toolkit

Companion tools for the book *The Software Field Guide* (DevelApp-ai/software-field-guide). `sfg` extracts architecture **truth from code** — not from what the diagrams claim — and renders editable, evidence-cited artifacts.

Five commands ship today, mapping to book issues [#54](https://github.com/DevelApp-ai/software-field-guide/issues/54), [#55](https://github.com/DevelApp-ai/software-field-guide/issues/55), [#58](https://github.com/DevelApp-ai/software-field-guide/issues/58), [#56](https://github.com/DevelApp-ai/software-field-guide/issues/56), and [#60](https://github.com/DevelApp-ai/software-field-guide/issues/60):

| Command | Book issue | What it does |
| --- | --- | --- |
| `sfg context-map` | #54 Architecture context mapper and boundary doctor | Scans a .NET solution, builds the context dependency graph, and flags reach-arounds, forbidden directions, shared-kernel sprawl, and orphaned seams — every finding with file:line evidence. Emits an editable drawio map in the book's diagram conventions. |
| `sfg seam-ledger` | #55 Extension-point ledger and seam evidence linter | Inventories every seam (interfaces, plugin contracts, event hooks) with consumer counts, flags one-implementation seams as speculative generality, checks versioning and default implementations, and renders the collapse-review checklist. The evidence bar is a required input, never a guess. |
| `sfg tenant-probes` | #58 Multitenant isolation probe generator | Scans an EF Core model (DbContext, `DbSet<T>` entities) and generates a compilable xunit probe suite: cross-tenant read probes, fail-closed save guards, pool-checkout session-leakage checks, and disabled-state fail-closed probes — one `[Fact]` per entity, all citing the scanned file:line inventory. Runs in CI so isolation regresses loudly. |
| `sfg cockpit` | #56 Strangler-fig migration cockpit | Reads the migration route table (`migration.json`), checks it against the routing seam (every declared route wired; no undeclared wires), runs the sunset tripwire (a strangled route whose old-implementation traffic stayed under the retirement threshold for the full sunset window — or the fig is not finished), and generates a compilable shadow-diff xunit suite comparing old and new implementations per declared tolerance. |
| `sfg events` | #60 Event contract conformance harness | Given a message contract (`events.json`), generates a compilable xunit suite that replays chaos-lite against the real consumer: duplicate delivery (exactly-once effects), out-of-order arrival, poison payloads (routed to the dead letters, never a crash), and old-schema producer replay (version-skew tolerance), plus outbox health checks on the producer side (publish waits pending; double dispatch stays idempotent). Seeded faults visibly fail a naive consumer and pass a compliant one — the #60 done-when. |

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

## Honest evaluation status

- **Syntax-level analysis.** The scanner uses Roslyn (`ParseText`) only — no `MSBuildWorkspace`, no compilation, no semantic binding. Usage resolution is by simple type name; when a name is declared in multiple projects the analysis skips it rather than guessing (an "ambiguous" count is in `context-map.json`).
- **No restore required.** Target solutions are never built, so the tool runs against codebases whose dependencies don't even compile on the scan machine. Attribute usages match by attribute name with a fallback suffix.
- **Deterministic output.** Ordinal sorting everywhere, relative paths, no timestamps — identical input produces byte-identical artifacts.
- **Honest findings.** Errors mean a configured boundary rule is violated; warnings are judgment calls for review. Nothing is silently filtered.
- **`tenant-probes` is EF-shaped and convention-checked.** The model scan is syntax-level (no compilation): a DbContext is a class deriving `DbContext`, an entity is a `DbSet<T>` property, and entity namespaces resolve by simple name (ambiguous or missing declarations are loud errors, never guesses). The probes exercise your conventions against a real SQLite in-memory database — they prove the mechanism exists, not that every query in your app is filtered. Pool-checkout checks stand in for `DbContext` pooling with fresh contexts over identical options; if your context pool resets tenant state via `IDbContextFactory`, wire that into the harness. Raw-SQL RLS probes (PostgreSQL `current_setting`-based) are a deliberate non-goal of this version.
- **`events` proves tolerance on replayed cases, not production behavior.** The convention scan is syntax-level: the consumer and outbox resolve by simple class/record name (missing or ambiguous declarations are loud errors). The probes replay deterministic envelopes — ids `case-N`, `skew-N`, `ob-N` — against freshly constructed consumers; they prove the tolerance mechanisms (dedup, dead-lettering, skew handling, outbox idempotency) exist and work on the declared cases, not that every production path is safe. Poison payloads are a fixed corpus (malformed JSON, a non-object message, a type-violating envelope); extend the corpus by adding cases, not by editing generated files.

- **`cockpit` reasons about claims, not proof of traffic.** The pivot and handler scans are syntax-level: wiring evidence is a string-literal route id (dynamic ids are skipped, never guessed), handler resolution is by simple class/record name (missing or ambiguous declarations are loud errors). The shadow-diff suite exercises the declared `old`/`new` handlers under the convention `string <Method>(string input)` with JSON in and JSON out; it proves the two implementations agree on the declared cases within the declared tolerance — it does not observe production traffic. The `traffic` history is data you attest to in the route table; the tripwires check it for consistency, not provenance.

## Demo

CI demonstrates both analysis commands against two public example codebases — [ErpLight](https://github.com/DevelApp-ai/ErpLight) (plugin-module ERP) and [RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory) (runtime pluggable class factory) — and uploads the generated maps, findings, and ledgers as workflow artifacts.

The tenant-probe demo is in-repo: [`demo/MultitenantApp`](demo/README.md), a small EF Core app with a per-tenant query filter and a fail-closed `SaveChanges` guard, plus two deliberately regressed variants (query filter removed; save guard removed). CI generates the probe suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** — the #58 done-when proof: isolation regressions fail loudly, by construction.

The cockpit demo is also in-repo: [`demo/StranglerApp`](demo/README.md), a small strangler-fig app whose `shipping.quote` route is strangled end to end — promoted at the routing pivot, shadow-diffed green against the retired legacy handler, and retired below the traffic threshold, so the sunset tripwire fires (the #56 done-when: one route strangled with the tripwire armed and firing). CI also runs three deliberately broken variants: a drifted new implementation (the shadow suite fails with `SHADOW DRIFT`), an unwired declared route (`migration/unwired-route` error), and a strangled route whose traffic contradicts the claim (`migration/strangle-unfinished` error).

The events demo is in-repo too: [`demo/EventApp`](demo/README.md), a small eventing app with a compliant `OrderEventsConsumer` (dedup, dead letters, old-schema tolerance) and `Outbox`, two event contracts — `order.placed` v2 with a v1 skew replay, and `invoice.issued` v1 — plus two deliberately regressed variants: a naive consumer (no dedup, crashes on poison, refuses old versions) and a fire-and-forget outbox (publishes vanish, dispatches leave no bookkeeping). CI generates the suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** with `EventConformance.ConsumerProbes` / `EventConformance.OutboxProbes` — the #60 done-when: seeded faults visibly fail a naive consumer and pass a compliant one.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md). Implemented: #54 (context mapper / boundary doctor), #55 (seam ledger / evidence linter), #58 (tenant-isolation probe generator), #56 (strangler-fig migration cockpit), #60 (event contract conformance harness).

## License

MIT — see [LICENSE](LICENSE).
