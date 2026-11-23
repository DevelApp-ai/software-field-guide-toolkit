# sfg review-calib

> Book issue #77 Reviewer calibration harness.

Measures the humans in the loop from a **recorded** export of review decisions — it never calls an API live and never ranks people. Computes catch rates on seeded defects per role, queue telemetry (approval rate and decision latency side by side — a queue that approves everything in under a second is a broken loop, not a review), and judge agreement on the recorded sample, and renders the rung-evidence table the autonomy ladder is earned on. Calibration instrument, not a leaderboard. Advisory by default; `--strict` exits non-zero on errors.

## Usage

```bash
# reviewer calibration harness: catch rates and queue telemetry from a recorded export
sfg review-calib /path/to/app --out review-calib
# review-calib also accepts --config review-calib.json (optional — every key
#   has a default) and --events to read a different recorded export (e.g. a
#   seeded broken one); --strict exits 1 on error-severity findings (default:
#   advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`review-calib` writes the calibration board:

- `review-calib.md` — the human board: catch rates by role (reviews, seeded, caught, rate), queue telemetry (approve/edit/reject/escalate counts, approval rate, median decision seconds — the rubber-stamp evidence), judge calibration (agreement on the recorded sample, disagreements named), and the rung-evidence table the autonomy ladder is earned on. A clean export renders the explicit `✅ Clean export` line.
- `review-calib.json` — the machine summary: generator, counts, per-role catch rates, per-queue telemetry, judge agreement, rung evidence, findings with rule/severity/message/evidence, error and warning counts.

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `calib/seed-missed` | error | a seeded defect was approved cleanly — the expected decision was not taken; cites the reviewer, role, queue, decision latency, and edit count |
| `calib/catch-rate-below` | error | a role's catch rate on seeded defects sits below the calibration floor (default 80%) |
| `calib/rubber-stamp-queue` | error | a queue approves at or above the rubber-stamp rate (default 99.8%) with a median decision latency under the floor (default 1.0s) across enough reviews (default 10) — this is not review, it is a broken loop |
| `calib/sub-second-approval` | warning | a queue's median decision latency is under a second — approvals this fast are clicks, not examinations; watch it before it becomes a broken loop |
| `calib/stale-queue` | warning | a queue has no seeded-defect decision within the staleness window (default 30 days) of the export's today — a queue that is never measured cannot be said to work |

## Honest status

- **`review-calib` measures recorded decisions, not reviewers.** The harness reads a committed export of review decisions — it never calls an API live, and it never ranks people: catch rates, approval telemetry, and judge agreement are calibration instruments for the queues and the autonomy ladder, not a leaderboard. The seeded defects are planted in the export; the rates measure the recorded window only. The judge column is a recorded verdict the human decisions are compared against — disagreement is named for calibration, never treated as failure. The harness proves the recorded window's evidence holds; it does not observe reviews live, and it does not prove the export itself was honest.

## In-repo demo

The review-calib demo is in-repo too: [`demo/ReviewCalibApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a recorded export of review decisions for a synthetic `example/checkout` repository — 20 reviews across two queues (`checkout`, `infra`) and two roles (`senior`, `junior`), eight seeded defects (all caught in the control), and a recorded judge that disagrees on `rv-005` so the agreement board shows real calibration (0.8 on the judged sample, disagreements named). Three deliberately broken variants from `demo/broken/` bite under `--strict` via `--events`: a seeded defect approved cleanly (`calib/seed-missed` — the #77 done-when, the one miss the instrument exists to catch), a role below the calibration floor (`calib/catch-rate-below` — junior catches 3 of 4, 75.0% against the 80% floor), and a rubber-stamping queue (`calib/rubber-stamp-queue` — checkout approves 12 of 12 at a 0.5 s median, plus `calib/stale-queue` on its unmeasured window). CI runs the control clean with every seed caught and the board byte-identical on regeneration.
