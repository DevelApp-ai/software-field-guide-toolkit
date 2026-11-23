# sfg eval

> Book issue #66 LLM evaluation harness runner and baseline gate.

Judges a **recorded** run (a committed `results.json`) against the suite's committed labeled set: aggregates per-stage metrics, calibrates the judge against the human-labeled sample (abstains never count as agreement), and gates against both the declared thresholds and the committed baseline — a regression cites the cases that moved, never just the aggregate. Dropped cases, undeclared cases, duplicate results, and stray metrics are loud errors. `--update-baseline` re-baselines deliberately. The harness never calls a model — wiring a live system means producing a results file in the documented format.

## Usage

```bash
# LLM eval harness: judge a recorded run against the labeled set and baseline
sfg eval /path/to/suite --out eval
# eval also accepts --config eval.json (see below); it defaults to
#   <root>/eval.json, --results results.json to judge a different recorded
#   run, and --update-baseline to rewrite the committed baseline from this
#   run (deliberate re-baselining); --strict exits 1 on error-severity
#   findings (default: advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`eval` writes the eval board:

- `eval.md` — the human board: per-stage metric table (observed vs threshold vs baseline, with verdict), judge agreement against the human-labeled sample (abstains never count; disagreements name the cases), and findings sorted errors-first with `file:line` citations — a regression cites the cases that moved.
- `eval.json` — the machine summary: run name, per-metric observed/threshold/baseline, agreement numbers, findings with rule/severity/message/evidence, error and warning counts.
- `baseline.json` (via `--update-baseline`) — the committed baseline: per-metric aggregate values plus per-case scores, so a future regression can cite exactly the cases that moved. Deterministic, ordinal-sorted, no timestamps.

## Inputs

## Eval suite (`eval.json`, the eval harness's required input)

```json
{
  "name": "docs-assistant-eval",
  "judge": "rule-based-judge-v1",
  "stages": [
    { "id": "retrieval", "metrics": ["recallAt3"] },
    { "id": "generation", "metrics": ["faithfulness", "refusalCorrectness"] }
  ],
  "thresholds": { "recallAt3": 0.8, "faithfulness": 0.9, "refusalCorrectness": 1.0 },
  "cases": "cases.json",
  "results": "results.json",
  "baseline": "baseline.json"
}
```

`stages` declares the pipeline stages and the metrics their cases are scored by — metric names are global (thresholds and baselines key on them). `thresholds` are the floor the suite declared; a threshold for an undeclared metric never passes silently. Paths resolve relative to the config's directory. `judge` is the judge configuration id, published with every report.

`cases.json` is the labeled set, committed with the suite:

```json
{ "cases": [
  { "id": "r1", "stage": "retrieval", "input": "query: ...", "expected": "docs/...", "human": "correct" }
] }
```

Cases carrying a `human` label (`correct`/`incorrect`) form the human-labeled sample the judge is calibrated against.

`results.json` is the recorded run to judge — the system under evaluation produces it (wiring a live system means producing this file in this format; the harness never calls a model):

```json
{ "run": "docs-assistant-v3", "results": [
  { "id": "r1", "predicted": "docs/...", "judge": "correct", "scores": { "recallAt3": 1.0 } }
] }
```

`judge` per result is the judge's verdict (`correct`/`incorrect`/`abstain`); scores are numbers in [0, 1] keyed by declared metric. `baseline.json` (written by `--update-baseline`) records per-metric aggregate values plus per-case scores, so a regression cites the cases that moved — never just the aggregate.

## Honest status

- **`eval` judges recorded runs; it never calls a model.** The harness reads a committed `results.json` your system (or your judge pipeline) produced in the documented format, aggregates per-stage metrics over the cases that have scores (unscored cases are excluded and flagged, never guessed), and gates against thresholds and the committed baseline. Judge agreement is measured against the human-labeled sample only — abstains never count as agreement, and disagreements are named for calibration, not treated as failures. Aggregates are means rounded to 4 decimal places (AwayFromZero); the gate compares the rounded value. The harness proves the run's recorded outcomes hold and are accounted for; it does not prove the recording itself was honest.

## In-repo demo

The eval demo is in-repo too: [`demo/EvalApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a docs-assistant suite with two stages (retrieval scored by `recallAt3`, generation scored by `faithfulness` and `refusalCorrectness`), nine cases — seven human-labeled, one of which the judge disagrees on so the agreement board shows real calibration (6/7 = 0.8571, disagreements named) — a committed baseline, and two deliberately broken recorded runs: a regression (r3 drops out of the top-3, g2's summary drifts — both metrics fall below baseline *and* threshold, `--strict` exits 1 with `eval/baseline-regression` citing the moved cases) and an undeclared case (r99 appears in the run, `eval/unknown-result`). CI runs all three — the control is clean with its calibration visible, the committed baseline is byte-identical to what `--update-baseline` writes, and each seeded failure bites — the #66 done-when: the seeded suite runs in CI, the seeded regression is caught with per-case evidence, judge agreement is reported against the human-labeled sample, and everything reproduces from a clean checkout.
