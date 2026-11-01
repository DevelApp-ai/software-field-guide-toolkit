# UnitEconApp — `sfg unit-econ` (#83)

A **SEEDED** cloud unit-economics estate: three tenants (acme, globex,
initech) on three services (api, render, pipeline) across two months
(2026-08, 2026-09). No real billing history backs these numbers — they
demonstrate the joins, not a cloud bill. The extractor reads recorded
exports only and never calls a live cloud API:

| Asset | What it holds |
| --- | --- |
| `unit-econ.json` | the estate contract: SEEDED-labeled, EUR, month granularity, and each tenant's per-period budget (acme 650, globex 550, initech 500) |
| `billing.json` | the recorded billing export: one line per service per month (api 720/840, render 300/360, pipeline 180/200) |
| `allocations.json` | the per-tenant allocation manifest: one rule per (tenant, service), each service's shares summing to exactly 1.0 |
| `telemetry.json` | the recorded request counts: two sampled dates per tenant/service/month, 36 rows |

Run it:

```bash
sfg unit-econ demo/UnitEconApp --out unitecon-good
```

The control is silent (`✅ Clean run`, 0 findings): every billed service is
allocated, every served request is priced, every join verified, and every
tenant is inside its budget. The board says **SEEDED** in its honesty line,
and regeneration is byte-identical. Trends, never scores — tenants are not
ranked; their cost-per-request series are published.

The broken variants live in [`../broken`](../broken) and CI runs them via
`--billing`/`--allocations`/`--telemetry`:

| Variant | How CI seeds it | Rule that bites |
| --- | --- | --- |
| `allocations.Dangling.json` | an extra rule prices `acme` on `search`, which the billing export never bills | `unitecon/dangling-allocation` — an allocation without a cost is a dangling join; `--strict` exits 1 |
| `telemetry.Unbilled.json` | telemetry serves requests on `search`, a service the billing export never bills | `unitecon/unbilled-service` — served but never billed, the unit cost is unknowable |
| `telemetry.Unpriced.json` | tenant `hooli` serves requests on billed `api` but no allocation rule prices it | `unitecon/unpriced-tenant` — requests without a price |
| `billing.SpendSpike.json` | api's September line jumps 840 → 1000, pushing only acme over budget (694 > 650; globex 532, initech 334 stay inside) | `unitecon/spend-spike` — the finding cites the config's declared budget line and acme's first allocation rule as per-tenant evidence |

Every bite cites `file:line` evidence. The control stays silent; each seeded
violation is caught exactly once — the #83 done-when.
