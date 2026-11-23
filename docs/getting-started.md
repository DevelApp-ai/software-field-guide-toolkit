# Getting started

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

# plan-conformance audit: intent diff vs implementation diff, advisory
sfg plan-audit /path/to/estate --spec-diff intent.diff --code-diff implementation.diff

# LLM eval harness: judge a recorded run against the labeled set and baseline
sfg eval /path/to/suite --out eval

# migration linter: expand/migrate/contract against the gates ledger
sfg migration-lint /path/to/app --out migration-lint

# DORA four keys: verified joins, trended series from a recorded export
sfg dora /path/to/app --out dora

# reviewer calibration harness: catch rates and queue telemetry from a recorded export
sfg review-calib /path/to/app --out review-calib

# bias probe pack: counterfactual consistency, cohort slices, refusal asymmetry
sfg bias-probe /path/to/app --out bias-probe

# resilience drill evidence harness: clocks, RTO/RPO, signals, freshness
sfg drills /path/to/app --out drills

# cloud unit economics: cost-per-request trends, budgets, verified joins
sfg unit-econ /path/to/app --out unit-econ

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
# plan-audit also accepts --config plan-audit.json (see below); it defaults to
#   <root>/plan-audit.json, and --semantic matches.json for externally
#   produced (e.g. LLM-assisted) spec-to-code matches; --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# eval also accepts --config eval.json (see below); it defaults to
#   <root>/eval.json, --results results.json to judge a different recorded
#   run, and --update-baseline to rewrite the committed baseline from this
#   run (deliberate re-baselining); --strict exits 1 on error-severity
#   findings (default: advisory, exit 0)
# migration-lint also accepts --config migration-lint.json (see below); it
#   defaults to <root>/migration-lint.json when the file exists, and --gates
#   to judge against a different gates ledger (e.g. a seeded broken one);
#   --strict exits 1 on error-severity findings (default: advisory, exit 0)
# dora also accepts --config dora.json (see below); it defaults to
#   <root>/dora.json when the file exists, and --events to read a different
#   recorded export (e.g. a seeded broken one); --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# review-calib also accepts --config review-calib.json (optional — every key
#   has a default) and --events to read a different recorded export (e.g. a
#   seeded broken one); --strict exits 1 on error-severity findings (default:
#   advisory, exit 0)
# bias-probe also accepts --config bias-probe.json (it defaults to
#   <root>/bias-probe.json), --results to judge a different recorded run
#   (e.g. a seeded broken one), and --update-baseline to rewrite the
#   committed baseline from this run (deliberate re-baselining); --strict
#   exits 1 on error-severity findings (default: advisory, exit 0)
# drills also accepts --config drills.json (it defaults to
#   <root>/drills.json) and --evidence to judge a different recorded
#   drill export (e.g. a seeded broken one); --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# unit-econ also accepts --config unit-econ.json (it defaults to
#   <root>/unit-econ.json) and --billing/--allocations/--telemetry to
#   judge different recorded exports (e.g. a seeded broken one);
#   --strict exits 1 on error-severity findings (default: advisory, exit 0)
```
