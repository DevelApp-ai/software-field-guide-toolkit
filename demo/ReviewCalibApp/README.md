# ReviewCalibApp — `sfg review-calib` (#77)

A recorded export of review decisions for a synthetic `example/checkout` repository: 20
reviews across two queues (`checkout`, `infra`) and two roles (`senior`, `junior`), with
eight seeded defects planted in the export (six in `checkout`, two in `infra`) and a
recorded judge that disagrees with the human on `rv-005` (agreement 0.8 on 5 judged rows) —
so the control board shows real calibration: every seeded defect caught, `checkout`
approving 57.1% at a 630 s median decision, `infra` 66.7% at 360 s. The harness reads the
recorded export — it never calls an API live, and it never ranks people: the board is a
calibration instrument for the queues and the autonomy ladder, not a leaderboard.

## The export format is the contract

`review-events.json` carries a `source` block (`repository`, `window`, `exportedAt`,
`today` — the staleness anchor, `note`) and one row per recorded review decision:

```json
{
  "id": "rv-002",
  "queue": "checkout",
  "reviewerRole": "junior",
  "reviewer": "jonas",
  "submittedAt": "2026-10-05T11:00:00Z",
  "decidedAt": "2026-10-05T11:05:00Z",
  "decision": "edit",
  "editsBeforeDecision": 2,
  "judgeAgrees": true,
  "seeded": { "seedId": "seed-01", "expectedDecision": "edit" }
}
```

- `decision` is one of `approve`, `edit`, `reject`, `escalate` — anything else is a loud
  error, never a guess.
- a seeded review is **caught** when the decision is not `approve` — a planted defect that
  sails through cleanly is the one miss the whole instrument exists to catch.
- `judgeAgrees` is the recorded judge's verdict on the human decision (`true`, `false`, or
  `null` for unjudged rows); disagreement is surfaced on the board, never scored as
  failure.

Run it locally from a checkout:

```bash
dotnet run --project src/sfg -- review-calib demo/ReviewCalibApp --out /tmp/reviewcalib-good
dotnet run --project src/sfg -- review-calib demo/ReviewCalibApp --out /tmp/reviewcalib-rerun
cmp /tmp/reviewcalib-good/review-calib.md /tmp/reviewcalib-rerun/review-calib.md   # byte-identical
```

## Broken variants

Three deliberately broken variants in [`../broken/`](../broken) bite under `--strict`
(via `--events`):

| Variant | What it seeds | What bites |
| --- | --- | --- |
| `review-events.SeedMissed.json` | `seed-09` approved cleanly | `calib/seed-missed` (error) — the #77 done-when |
| `review-events.LowCatch.json` | junior misses a seeded defect | `calib/catch-rate-below` (error) — 3 of 4 = 75.0% under the 80% floor |
| `review-events.RubberStamp.json` | `checkout` approves 12 of 12 at a 0.5 s median | `calib/rubber-stamp-queue` (error) + `calib/stale-queue` (warning) on the unmeasured window |

CI runs the control clean (every seed caught, `✅ Clean export`), regenerates the board
byte-identically, then asserts each variant exits 1 under `--strict` with its cited
`file:line` evidence into the export — the #77 done-when: the control stays clean, each
seeded failure bites by construction, deterministically.