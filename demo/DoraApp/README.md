# DoraApp — `sfg dora` (#68)

A recorded export of a **real** public repository's October 2025 release train:
[DevelApp-ai/RuntimePluggableClassFactory](https://github.com/DevelApp-ai/RuntimePluggableClassFactory),
the 12 releases from v2.0.2 (2025-10-26) to v2.1.0 (2025-11-04) — the known
prerelease-to-release cadence the issue asked for. Everything the extractor
reads is committed here: `dora-events.json` records 12 deployments, 13 merged
changes, 46 CI runs, and the one failure visible in the public history, plus
the `dora-definitions.json` contract the series is measured against.

The incident is the estate's story: **v2.0.7 broke the GitHub Pages deploy**
(Jekyll Liquid syntax conflicts in the Leap Day theme). It was detected from
the recorded incident review at 2025-10-26 21:30Z and restored at
2025-10-27 00:20:40Z — MTTR 2.8 h — by shipping hotfix PR #18 in v2.0.8, which is why
the failure row carries mode `hotfix` with `hotfixPr: 18` evidence. The
extractor verifies that evidence: PR #18 merged at 00:19:36Z, inside the
incident window.

Run it:

```bash
sfg dora demo/DoraApp --out dora-good
```

The control is silent (`✅ Clean export`), the joins table shows every join
verified against the recorded data (deployment → change → build, failure →
restore evidence), and the four keys trend per ISO week:

| Week | Deployments | Changes shipped | CFR | MTTR |
| --- | --- | --- | --- | --- |
| 2025-10-20 | 6 | 8 | 16.7% | 2.8 h |
| 2025-10-27 | 4 | 3 | 0.0% | — |
| 2025-11-03 | 2 | 2 | 0.0% | — |

Two honest observations the board reports rather than hides: lead times are
**minutes** (releases are tagged on merge by the GitVersion pipeline, so a
change ships ~1 minute after it merges), and v2.0.9 is a direct commit with
no PR — an *empty* `prs` list is legal because the extractor verified it
empty, not because it skipped it.

## Producing the export

The extractor never calls an API live — wiring a live system means producing
an export in this documented format. This one was recorded with plain `curl`
against the unauthenticated GitHub API:

```bash
api=https://api.github.com/repos/DevelApp-ai/RuntimePluggableClassFactory
curl -s "$api/releases?per_page=100" > releases.json          # deployments
curl -s "$api/pulls?state=closed&per_page=100" > pulls.json   # changes
curl -s "$api/actions/runs?per_page=100" > runs.json          # builds
# tag compares confirm each release's PR range and merge shas
```

Failure rows are recorded from incident review — they are the one input the
API cannot hand you, which is exactly why the extractor demands the evidence
be checked, never trusted.

## The broken variants

The variants live in [`../broken`](../broken) and CI passes them via
`--events`; each one is caught by construction — the #68 done-when:

| Variant | How CI seeds it | Rule that bites |
| --- | --- | --- |
| `dora-events.ReRun.json` | v2.0.8 re-deployed an hour later, recorded as a new deployment | `dora/re-run-mislabeled` — same environment + same target sha is one deployment, the later row is excluded from every count |
| `dora-events.DanglingJoin.json` | v2.1.0 claims to ship PR #99 | `dora/dangling-join` — the PR does not exist in the changes |
| `dora-events.Unbuilt.json` | v2.0.12 shipped a sha no green run observes | `dora/unbuilt-deployment` — the release is unbuilt |
| `dora-events.UnknownMode.json` | the incident's restore mode is `redeploy` | `dora/unknown-failure-mode` — not in the taxonomy, so the failure drops out of CFR and MTTR |
| `dora-events.Unevidenced.json` | the hotfix's `hotfixPr: 999` | `dora/unevidenced-failure` — the evidence must resolve inside the incident window |

Every bite cites `file:line` into the export it flags. Trends, not scores: the
board carries the `dora-definitions-v1` version, never a benchmark label.
