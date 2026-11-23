# sfg context-map

> Book issue #54 Architecture context mapper and boundary doctor.

Scans a .NET solution, builds the context dependency graph, and flags reach-arounds, forbidden directions, shared-kernel sprawl, and orphaned seams — every finding with file:line evidence. Emits an editable drawio map in the book's diagram conventions.

## Usage

```bash
# context map: drawio + json + findings report
sfg context-map /path/to/solution
# common flags (--config, --out, --strict): see the Getting started page
```

The two analysis commands share one syntax-level scan engine — a seam in the ledger and a contract in the map are the same underlying type.

## Outputs

`context-map` writes:

- `context-map.drawio` — deterministic drawio context map (house palette: host blue, plugin orange, shared-kernel yellow; green edges clean, red dashed edges forbidden). Same input, byte-identical file — safe to diff and commit.
- `context-map.json` — machine-readable contexts, edges, and findings.
- `findings.md` — findings table sorted errors-first, each row citing `file:line`.

## Inputs

## Configuration (`sfg.json`, all keys optional)

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

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `boundary/direction-violation` | error | a context consumes another across a configured forbidden edge (e.g. plugin → plugin) |
| `boundary/reach-around` | warning | a context uses a public type from another context that sits outside its contract surface |
| `boundary/orphaned-seam` | warning | a published contract has no consumers outside its own context |
| `boundary/shared-kernel-sprawl` | warning | a shared-kernel contract has exactly one consuming context — candidate to move closer to its user |

## Honest status

- **Syntax-level analysis.** The scanner uses Roslyn (`ParseText`) only — no `MSBuildWorkspace`, no compilation, no semantic binding. Usage resolution is by simple type name; when a name is declared in multiple projects the analysis skips it rather than guessing (an "ambiguous" count is in `context-map.json`).

- **No restore required.** Target solutions are never built, so the tool runs against codebases whose dependencies don't even compile on the scan machine. Attribute usages match by attribute name with a fallback suffix.

- **Deterministic output.** Ordinal sorting everywhere, relative paths, no timestamps — identical input produces byte-identical artifacts.

- **Honest findings.** Errors mean a configured boundary rule is violated; warnings are judgment calls for review. Nothing is silently filtered.

## In-repo demo

CI demonstrates both analysis commands against two public example codebases — [ErpLight](https://github.com/DevelApp-ai/ErpLight) (plugin-module ERP) and [RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory) (runtime pluggable class factory) — and uploads the generated maps, findings, and ledgers as workflow artifacts.
