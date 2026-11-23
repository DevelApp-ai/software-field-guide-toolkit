# sfg seam-ledger

> Book issue #55 Extension-point ledger and seam evidence linter.

Inventories every seam (interfaces, plugin contracts, event hooks) with consumer counts, flags one-implementation seams as speculative generality, checks versioning and default implementations, and renders the collapse-review checklist. The evidence bar is a required input, never a guess.

## Usage

```bash
# seam ledger: json ledger + collapse-review checklist
sfg seam-ledger /path/to/solution
# seam-ledger also accepts --evidence evidence.json (seam evidence entries)
# common flags (--config, --out, --strict): see the Getting started page
```

The two analysis commands share one syntax-level scan engine — a seam in the ledger and a contract in the map are the same underlying type.

## Outputs

`seam-ledger` writes:

- `seam-ledger.json` — the versioned complexity ledger: one entry per seam with kind (`interface` / `plugin-contract` / `event-hook`), file:line, implementations, consumer contexts, usage sites, default implementation (with registration site), `versioned` flag, speculative-generality flag, and evidence status.
- `collapse-review.md` — the checklist: which seams are **due for a collapse decision** (and why), which to monitor, and which fail the evidence bar.

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

## Evidence file (`evidence.json` for `seam-ledger --evidence`)

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

| Check | Meaning |
| --- | --- |
| `speculativeGenerality` | one implementation — the seam may be a guess |
| `collapseReview.due` | seam has ≤1 consumer context and ≤1 implementation: collapse it, publish it deliberately, or delete it (reasons listed per seam) |
| `versioned` | seam surface carries a `Version` member or version-typed contract |
| `defaultImplementation` | implementation registered in DI-style code, else conventional name match (seam minus `I`) — cites the registration file:line |
| `evidenceComplete` | required evidence entry present and complete |

## Honest status

- **Syntax-level analysis.** The scanner uses Roslyn (`ParseText`) only — no `MSBuildWorkspace`, no compilation, no semantic binding. Usage resolution is by simple type name; when a name is declared in multiple projects the analysis skips it rather than guessing (an "ambiguous" count is in `context-map.json`).

- **No restore required.** Target solutions are never built, so the tool runs against codebases whose dependencies don't even compile on the scan machine. Attribute usages match by attribute name with a fallback suffix.

- **Deterministic output.** Ordinal sorting everywhere, relative paths, no timestamps — identical input produces byte-identical artifacts.

- **Honest findings.** Errors mean a configured boundary rule is violated; warnings are judgment calls for review. Nothing is silently filtered.

## In-repo demo

CI demonstrates both analysis commands against two public example codebases — [ErpLight](https://github.com/DevelApp-ai/ErpLight) (plugin-module ERP) and [RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory) (runtime pluggable class factory) — and uploads the generated maps, findings, and ledgers as workflow artifacts.
