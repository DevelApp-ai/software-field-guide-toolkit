# sfg plan-audit

> Book issue #64 Plan-conformance auditor.

Reconciles the approved spec diff (the intent PR's `.ai`/`.test` changes) against the generated code diff (the implementation PR) through a declared spec-to-code coverage map, and reports the two discrepancy classes — **omission** (an approved spec change no generated change implements) and **unapproved addition** (a generated change with no spec basis) — line-anchored on both sides, ranked errors-first and capped. Advisory by default; `--strict` exits non-zero on errors. LLM-assisted semantic matches are accepted only with valid anchors on both sides and are always labeled.

## Usage

```bash
# plan-conformance audit: intent diff vs implementation diff, advisory
sfg plan-audit /path/to/estate --spec-diff intent.diff --code-diff implementation.diff
# plan-audit also accepts --config plan-audit.json (see below); it defaults to
#   <root>/plan-audit.json, and --semantic matches.json for externally
#   produced (e.g. LLM-assisted) spec-to-code matches; --strict exits 1 on
#   error-severity findings (default: advisory, exit 0)
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`plan-audit` writes the conformance board:

- `plan-audit.md` — the human board: the approved spec change and the generated change side by side (each changed file with its line anchor and mapped area), the matches that reconcile them (machine matches from the coverage map; semantic matches labeled with their note), and the findings sorted errors-first with `file:line` citations on both sides.
- `plan-audit.json` — the machine summary: spec and code files with anchors and areas, matches with provenance, findings with rule/severity/anchors, error and warning counts.

## Inputs

## Plan-audit map (`plan-audit.json`, the plan auditor's required input)

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

`map` declares the spec-to-code coverage: intent files under a `spec` prefix are implemented by code under the paired `code` prefix — that pairing is the machine reconciliation unit. `allowedUnmappedCode` lists code paths that may change without a spec basis (build files, docs). `maxFindings` (default 50) caps the rendered findings; the hidden count is named. Overlapping spec or code prefixes never pass silently — an ambiguous map is a loud error.

The optional `--semantic matches.json` file declares externally produced spec-to-code correspondences (an LLM pass is the expected producer):

```json
{
  "matches": [
    { "spec": "specs/quote/quote-order.ai:11", "code": "src/QuoteTree/Billing/AuditTrail.cs:1",
      "note": "R5's audit trail is implemented by the new AuditTrail class, outside the declared quote-area map." }
  ]
}
```

Every semantic match must anchor a line the spec diff actually changes to a line the code diff actually changes, cite changed files on both sides, and is rendered labeled `semantic` — never a silent guess.

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `plan/omission` | error | an approved spec change in a mapped area has no generated change under the area it maps to — the intent was never implemented |
| `plan/unapproved-addition` | error | a generated change in a mapped area has no spec basis — scope drift, an invented feature, or hallucinated cleanup |
| `plan/unmapped-spec` | warning | a changed spec file matches no map entry — the coverage map does not cover this intent |
| `plan/unmapped-code` | warning | a changed code file matches no map entry and no allowed-unmapped prefix |

## Honest status

- **`plan-audit` reconciles coverage, not semantics.** Machine matches are area-level: a changed spec file and a changed code file in mapped areas reconcile each other — the audit proves every changed file on both sides is accounted for, not that a change is the right implementation of a requirement. Line anchors cite the first added line (deletion-only changes anchor the hunk start). Semantic matches are externally produced (an LLM pass is the expected producer), accepted only when both anchors are lines the diffs actually change, and always labeled `semantic` — the tool never invents a reconciliation. The audit is advisory by design: `--strict` makes error findings a non-zero exit where a team wants a gate.

## In-repo demo

The plan-audit demo is in-repo too: [`demo/PlanAudit`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a small QuoteTree estate with two spec areas (quote, billing) mapped to their code areas and four committed diff pairs: a conformant control, an omission (the billing spec gains "invoices are exportable as PDF" that no generated change implements), an unapproved addition (a surprise fee appears in `InvoiceService`), and a cross-area pair (the audit-trail requirement implemented in a new file outside the map's coverage) that only a labeled semantic match reconciles. CI runs all four — the control is silent, each seeded discrepancy fires with its line-anchored citation, and regeneration is byte-identical — the #64 done-when: the auditor bites on omission and unapproved addition and stays silent on a conformant pair, deterministically.
