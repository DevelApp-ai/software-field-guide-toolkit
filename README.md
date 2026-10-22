# Software Field Guide Toolkit

Companion tools for the book *The Software Field Guide* (DevelApp-ai/software-field-guide). `sfg` extracts architecture **truth from code** — not from what the diagrams claim — and renders editable, evidence-cited artifacts.

Two commands ship today, mapping to book issues [#54](https://github.com/DevelApp-ai/software-field-guide/issues/54) and [#55](https://github.com/DevelApp-ai/software-field-guide/issues/55):

| Command | Book issue | What it does |
| --- | --- | --- |
| `sfg context-map` | #54 Architecture context mapper and boundary doctor | Scans a .NET solution, builds the context dependency graph, and flags reach-arounds, forbidden directions, shared-kernel sprawl, and orphaned seams — every finding with file:line evidence. Emits an editable drawio map in the book's diagram conventions. |
| `sfg seam-ledger` | #55 Extension-point ledger and seam evidence linter | Inventories every seam (interfaces, plugin contracts, event hooks) with consumer counts, flags one-implementation seams as speculative generality, checks versioning and default implementations, and renders the collapse-review checklist. The evidence bar is a required input, never a guess. |

Both commands share one syntax-level scan engine, so a seam in the ledger and a contract in the map are the same underlying type.

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

# both accept:
#   --config sfg.json       analysis configuration (see below)
#   --out DIR               output directory (default sfg-out/<command>)
#   --strict                exit 1 if any error-severity finding (context-map)
#   --evidence evidence.json seam evidence entries (seam-ledger)
```

### Outputs

`context-map` writes:

- `context-map.drawio` — deterministic drawio context map (house palette: host blue, plugin orange, shared-kernel yellow; green edges clean, red dashed edges forbidden). Same input, byte-identical file — safe to diff and commit.
- `context-map.json` — machine-readable contexts, edges, and findings.
- `findings.md` — findings table sorted errors-first, each row citing `file:line`.

`seam-ledger` writes:

- `seam-ledger.json` — the versioned complexity ledger: one entry per seam with kind (`interface` / `plugin-contract` / `event-hook`), file:line, implementations, consumer contexts, usage sites, default implementation (with registration site), `versioned` flag, speculative-generality flag, and evidence status.
- `collapse-review.md` — the checklist: which seams are **due for a collapse decision** (and why), which to monitor, and which fail the evidence bar.

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

## Demo

CI demonstrates both commands against two public example codebases — [ErpLight](https://github.com/DevelApp-ai/ErpLight) (plugin-module ERP) and [RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory) (runtime pluggable class factory) — and uploads the generated maps, findings, and ledgers as workflow artifacts.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md). Implemented: #54 (context mapper / boundary doctor), #55 (seam ledger / evidence linter). Next per the book's build order: #56, #58, #60.

## License

MIT — see [LICENSE](LICENSE).
