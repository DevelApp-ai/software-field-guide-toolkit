# sfg unit-econ

> Book issue #83 Cloud unit economics extractor.

Joins a recorded billing export to a per-tenant allocation manifest and recorded request telemetry: cost-per-request trends per tenant and service, spend against declared per-period budgets, and a spend spike with per-tenant evidence. Every join is verified in both directions (the #68 discipline): an allocation rule must point at a billed service, a billed service must reach a tenant, a served service must be billed, a served tenant must be priced, and shares must sum to one. Trends, never scores — tenants are not ranked. Recorded exports only, never a live cloud API; a seeded estate is labeled SEEDED on every board. Advisory by default; `--strict` exits non-zero on errors.

## Usage

```bash
# cloud unit economics: cost-per-request trends, budgets, verified joins
sfg unit-econ /path/to/app --out unit-econ
# unit-econ also accepts --config unit-econ.json (it defaults to
#   <root>/unit-econ.json) and --billing/--allocations/--telemetry to
#   judge different recorded exports (e.g. a seeded broken one);
#   --strict exits 1 on error-severity findings (default: advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`unit-econ` writes the cloud unit economics board:

- `unit-econ.md` — the human board: per-tenant spend against declared budgets (status per period), cost-per-request per tenant/service/period (allocated cost, requests, unit price), and findings sorted errors-first with `file:line` citations. The board always carries its honesty line — a SEEDED estate says so, a real one says recorded exports only — and `sfg unit-econ` never calls a live cloud API. A clean run renders the explicit `✅ Clean run` line.
- `unit-econ.json` — the machine summary: generator, estate, export/manifest/source ids, currency, granularity, seeded flag, spend rows, cost-per-request rows, findings with rule/severity/message/evidence, error and warning counts.

## Inputs

## Unit economics harness (`unit-econ.json`, the unit-econ extractor's required input)

```json
{
  "name": "seeded-unit-economics",
  "seeded": true,
  "currency": "EUR",
  "granularity": "month",
  "billing": "billing.json",
  "allocations": "allocations.json",
  "telemetry": "telemetry.json",
  "budgets": {
    "acme": 650,
    "globex": 550,
    "initech": 500
  }
}
```

`seeded` is mandatory — the honesty is explicit, never guessed: a demo estate must say it is seeded, a real one must say it is not, and every board repeats it. `granularity` is the trend and budget bucket (`day`, `week`, or `month`; default `month`; weeks are ISO Mondays). Paths resolve relative to the config's directory. `budgets` are per-period spend thresholds per tenant, inclusive at the boundary; a tenant with spend but no declared budget is an advisory finding — a spike on it cannot be caught.

`billing.json` is the recorded billing export (an optional `currency` must match the config's), `allocations.json` the per-tenant allocation manifest (one rule per tenant and service — duplicates are a loud error; shares are fractions of one and a service's shares are verified to sum to it), and `telemetry.json` the recorded request counts (rows are additive samples):

```json
{ "export": "billing-export-2026-10", "currency": "EUR",
  "lineItems": [
    { "service": "api", "period": "2026-09-01", "amount": 840.00 }
  ] }
```

```json
{ "manifest": "tenant-allocation-v3",
  "rules": [
    { "tenant": "acme", "service": "api", "share": 0.50 }
  ] }
```

```json
{ "source": "gateway-telemetry-2026-10",
  "requests": [
    { "tenant": "acme", "service": "api", "date": "2026-09-07", "requests": 7100 }
  ] }
```

Money renders at 2 decimal places, cost-per-request at 4 (AwayFromZero); the budget gate compares the rounded spend. Trends, never scores: tenants are not ranked, their unit-cost series are published.

## Honest status

- **`unit-econ` joins recorded exports; it never calls a cloud API live.** The extractor reads a committed billing export, allocation manifest, and telemetry — it does not observe a cloud bill, and it does not prove the exports themselves were honest. Every join is verified in both directions: a dangling allocation, an unallocated cost, an unbilled service, an unpriced tenant, and over-allocated shares error; an allocation gap, an unbudgeted tenant, and idle capacity are advisory by design — the drift is published, not dropped. Budgets are inclusive at the boundary and compared against the rounded spend; a spike cites the config's declared budget line and the tenant's allocation rule as per-tenant evidence. Trends, never scores: tenants are not ranked, and a seeded estate is labeled SEEDED on every board — no real billing history backs a demo.

## In-repo demo

The unit-econ demo is in-repo too: [`demo/UnitEconApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a **SEEDED** cloud unit-economics estate — three tenants (acme, globex, initech) on three services (api, render, pipeline) across two months, with each service's allocation shares summing to exactly one and every tenant inside its declared budget (labeled SEEDED in the README and on every board: no real billing history backs these numbers). CI runs the control clean (every join verified, byte-identical on regeneration), then asserts four deliberately broken variants from `demo/broken/` bite under `--strict` via `--billing`/`--allocations`/`--telemetry`: a dangling allocation (an extra rule prices the never-billed `search` — `unitecon/dangling-allocation`, the #83 done-when), an unbilled service (telemetry serves `search` — `unitecon/unbilled-service`), an unpriced tenant (`hooli` serves requests no rule prices — `unitecon/unpriced-tenant`), and a spend spike (api's September line jumps 840 → 1000, pushing only acme over its 650 budget with per-tenant evidence — `unitecon/spend-spike`).
