# sfg dora

> Book issue #68 DORA four-key extractor.

Extracts the DORA four keys from a **recorded** events export (deployments, changes, builds, failures) — it never calls an API live. Verifies every join the keys depend on (deployment → change → build, failure → restore evidence), flags re-runs mislabeled as new deployments (excluded from every count), and trends deployment frequency, lead time, change failure rate, and restore time per day, ISO week, or month against a versioned definitions file. Trends, not scores. Advisory by default; `--strict` exits non-zero on errors.

## Usage

```bash
# DORA four keys: verified joins, trended series from a recorded export
sfg dora /path/to/app --out dora
# dora also accepts --config dora.json (see below); it defaults to
#   <root>/dora.json when the file exists, and --events to read a different
#   recorded export (e.g. a seeded broken one); --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`dora` writes the four-key board:

- `dora.md` — the human board: the joins table (every join the four keys depend on, with verified/total counts and the rule that guards it), the per-period time series (deployments, changes shipped, lead time, failure rate, restore time), the excluded re-runs, and findings sorted errors-first with `file:line` citations into the export. A clean export renders the explicit `✅ Clean export` line.
- `dora.json` — the machine summary: generator, definition version, counts, verified joins, the series, findings with rule/severity/message/evidence, error and warning counts.
- `dora-definitions.json` — the versioned definitions file (`dora-definitions-v1`): the four keys, the joins, the failure taxonomy (revert/rollback/hotfix and the evidence each demands), the re-run rule, the periods, and the trend policy. Deterministic and byte-identical on regeneration — commit it next to the export it defines.

## Inputs

## DORA events export and config (`dora-events.json`, `dora.json`, the extractor's inputs)

The extractor reads a committed, **recorded** export — it never calls an API live; wiring a live system means producing an export in the documented format. `dora.json` is optional (`events` and `period` default as shown; `maxFindings` caps the rendered findings list):

```json
{
  "events": "dora-events.json",
  "period": "week"
}
```

`dora-events.json` records the four event classes the keys join — `deployments` (each with the PRs its compare range says it ships), `changes` (merged PRs with their merge shas), `builds` (CI runs observing shas; attempts deduplicate per workflow per sha), and `failures` (incidents with their restore taxonomy: `revert`/`rollback`/`hotfix` and the evidence each mode demands — a revert or hotfix names the PR merged inside the incident window; a rollback names the earlier deployment of a different target sha):

```json
{
  "deployments": [
    { "id": "v2.1.0", "environment": "release", "deployedAt": "2025-11-04T19:18:16Z",
      "targetSha": "5a6ac5ca352cd3423820e87746e0bfa2fc1a9509", "prs": [22] }
  ],
  "changes": [
    { "pr": 22, "title": "Fix semantic versioning and test plugin loading",
      "mergedAt": "2025-11-04T19:17:18Z", "mergeSha": "5a6ac5ca352cd3423820e87746e0bfa2fc1a9509" }
  ],
  "builds": [
    { "run": 144, "workflow": ".NET CI/CD", "headSha": "5a6ac5ca352cd3423820e87746e0bfa2fc1a9509",
      "conclusion": "success", "completedAt": "2025-11-04T19:18:10Z" }
  ],
  "failures": [
    { "id": "leap-day-theme-liquid-conflict", "mode": "hotfix", "deployment": "v2.0.7",
      "detectedAt": "2025-10-26T21:30:00Z", "restoredAt": "2025-10-27T00:20:40Z",
      "evidence": { "hotfixPr": 18 } }
  ]
}
```

Timestamps are ISO 8601 UTC; a missing array, an invalid timestamp, or a duplicate id is a loud error — never a guess. An empty `prs` list is legal (a verified-empty direct commit). `--events` reads a different recorded export without touching the committed one.

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `dora/dangling-join` | error | a deployment ships a PR that is not in the changes — the deployment→change join dangles |
| `dora/temporal-join-violation` | error | a change merged after the deployment that claims to ship it — a cause cannot land after its effect |
| `dora/unbuilt-deployment` | error | a deployment's target sha was never observed by a successful CI run — the release is unbuilt |
| `dora/re-run-mislabeled` | error | two deployments of the same environment at the same target sha — the later row is a re-run mislabeled as a new deployment; flagged and excluded from every count |
| `dora/unknown-failure-mode` | error | a failure's restore mode is outside the taxonomy (revert, rollback, hotfix) — an unclassifiable restore is not a restore yet |
| `dora/unevidenced-failure` | error | a failure's mode evidence does not check out — the revert/hotfix PR does not resolve inside the incident window, or the rollback does not target an earlier deployment of a different sha |
| `dora/dangling-failure-join` | error | a failure names a deployment that does not exist |
| `dora/restore-before-detection` | error | a failure was restored before it was detected — service cannot return before it left |
| `dora/change-without-build` | warning | a merged change's merge sha was never observed by a CI run — the change is unverified by the pipeline |
| `dora/unshipped-change` | warning | a merged change is shipped by no deployment — it never reached an environment in this export |

## Honest status

- **`dora` reads a recorded export; the joins are verified, not observed.** The extractor never calls an API live — wiring a live system means producing an export in the documented format. It checks the export's internal consistency (deployment → change → build, failure → restore evidence) against the recorded data; it does not observe production, and the failure rows are recorded from incident review, which is exactly why their taxonomy evidence is checked rather than trusted. The series carries the `dora-definitions-v1` version — trends, not scores: no benchmark labels, ever.

## In-repo demo

The DORA demo is in-repo too: [`demo/DoraApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a recorded export of a **real** public repository — DevelApp-ai/RuntimePluggableClassFactory's October 2025 release train (v2.0.2 → v2.1.0, 12 releases in 10 days) — with 13 merged changes, 46 CI runs, and the one failure visible in the public history: v2.0.7 broke the GitHub Pages deploy, hotfixed by PR #18 in v2.0.8 (mode `hotfix`, MTTR 2.8 h, evidence verified inside the incident window). v2.0.9 is a direct commit with no PR — an empty `prs` list is legal because the extractor verified it empty, not because it skipped it. Five deliberately broken variants from `demo/broken/` bite under `--strict` via `--events`: a re-run mislabeled as a new deployment (`dora/re-run-mislabeled`, excluded from every count — the #68 done-when), a dangling PR join, an unbuilt release, an unknown restore mode, and unevidenced hotfix evidence. CI runs the control clean with the joins table verified and the committed definitions file byte-identical on regeneration.
