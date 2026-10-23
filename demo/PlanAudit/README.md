# PlanAudit demo — the plan-conformance auditor (#64)

A small demonstration estate for `sfg plan-audit`: the **QuoteTree** service with two spec
areas (quote, billing) mapped to the code areas that implement them, plus four committed
diff pairs — one conformant control, two seeded discrepancies, and one cross-area pair that
only a labeled semantic match can reconcile.

## The estate

```
estate/
  plan-audit.json            the spec-to-code coverage map (the auditor's required input)
  specs/quote/               quote-order.ai (the intent) + quote-order.test (the acceptance criteria)
  specs/billing/             invoice.ai + invoice.test
  src/QuoteTree/Quote/       QuoteService.cs — implements specs/quote/
  src/QuoteTree/Billing/     InvoiceService.cs — implements specs/billing/
```

`plan-audit.json` declares the machine-checkable coverage:

```json
{
  "project": "src/QuoteTree/QuoteTree.csproj",
  "map": [
    { "spec": "specs/quote/", "code": "src/QuoteTree/Quote/" },
    { "spec": "specs/billing/", "code": "src/QuoteTree/Billing/" }
  ],
  "allowedUnmappedCode": [ "src/QuoteTree/QuoteTree.csproj" ]
}
```

## The four variants

Each variant is a pair of diffs — the approved spec change (the intent PR) and the
generated change (the implementation PR):

| Variant | Intent diff | Implementation diff | What the audit proves |
| --- | --- | --- | --- |
| control | R4 "prices include VAT" + its acceptance criterion | `QuoteService` implements VAT | a conformant pair stays silent: `Conformant`, 0 errors, exit 0 |
| omission | also adds billing R4 "invoices are exportable as PDF" | only `QuoteService` changes | exactly one `plan/omission` error, anchored `specs/billing/invoice.ai:10` |
| addition | only the quote R4 | `QuoteService` **and** a surprise fee in `InvoiceService` | exactly one `plan/unapproved-addition` error, anchored `src/QuoteTree/Billing/InvoiceService.cs:7` |
| semantic | quote R5 "every recalculation is written to an audit trail" | a new `AuditTrail.cs` under Billing — outside the quote area | without `--semantic` both errors fire; with `semantic.json` the labeled match (`provenance: "semantic"`) reconciles both sides |

The audit is **advisory by default** (exit 0, findings reported); `--strict` exits 1 on
error-severity findings.

## Run it

```bash
sfg plan-audit demo/PlanAudit/estate \
  --spec-diff demo/PlanAudit/diffs/control.spec.patch \
  --code-diff demo/PlanAudit/diffs/control.code.patch \
  --out planaudit-control

# the other variants: swap in the omission/addition/semantic diff pairs
# the cross-area pair also takes: --semantic demo/PlanAudit/semantic.json
```

Outputs: `plan-audit.md` (the human board — changed files, matches, findings, all with
line anchors on both sides) and `plan-audit.json` (the machine summary). Same input,
byte-identical output.

## What the audit proves — and what it does not

- **Machine matches reconcile coverage, not semantics.** A changed spec file and a changed
  code file in mapped areas match each other: the audit proves every changed file on both
  sides is accounted for, not that a given change is the right implementation of a given
  requirement.
- **Line anchors cite the first added line** (deletion-only changes anchor the hunk start) —
  an anchor for a human to jump to, not a semantic claim about that exact line.
- **Semantic matches are externally produced** (an LLM pass is the expected producer,
  `semantic.json` here). They are accepted only when both anchors are lines the diffs
  actually change, and they are always labeled `semantic` — the tool never invents a
  reconciliation.
- The diffs are committed fixtures shaped exactly like `git diff` output; the auditor reads
  real unified diffs, but this demo does not generate them from a real PR pair.
