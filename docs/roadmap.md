# Toolkit roadmap

The toolkit implements the companion-tool roadmap from book tracking issue [#53](https://github.com/DevelApp-ai/software-field-guide/issues/53) (round 1) and [#65](https://github.com/DevelApp-ai/software-field-guide/issues/65) (round 2) in DevelApp-ai/software-field-guide. Each issue lands as its own PR against `main`; Lars merges.

| Book issue | Component | Command | Status |
| --- | --- | --- | --- |
| [#54 Architecture context mapper and boundary doctor](https://github.com/DevelApp-ai/software-field-guide/issues/54) | `Sfg.Toolkit.Core` + `Sfg.Toolkit.ContextMap` | `sfg context-map` | done — this PR |
| [#55 Extension-point ledger and seam evidence linter](https://github.com/DevelApp-ai/software-field-guide/issues/55) | `Sfg.Toolkit.SeamLedger` | `sfg seam-ledger` | done — this PR |
| [#56](https://github.com/DevelApp-ai/software-field-guide/issues/56) | `Sfg.Toolkit.Cockpit` | `sfg cockpit` | done — this PR |
| [#58](https://github.com/DevelApp-ai/software-field-guide/issues/58) | `Sfg.Toolkit.TenantProbes` | `sfg tenant-probes` | done — this PR |
| [#60](https://github.com/DevelApp-ai/software-field-guide/issues/60) | `Sfg.Toolkit.EventHarness` | `sfg events` | done — this PR |
| [#64 Plan-conformance auditor](https://github.com/DevelApp-ai/software-field-guide/issues/64) | `Sfg.Toolkit.PlanAudit` | `sfg plan-audit` | done — this PR |
| [#66 LLM evaluation harness runner and baseline gate](https://github.com/DevelApp-ai/software-field-guide/issues/66) | `Sfg.Toolkit.EvalHarness` | `sfg eval` | done — this PR |
| [#67 Expand-migrate-contract migration linter](https://github.com/DevelApp-ai/software-field-guide/issues/67) | `Sfg.Toolkit.MigrationLint` | `sfg migration-lint` | done — this PR |
| [#68 DORA four-key extractor](https://github.com/DevelApp-ai/software-field-guide/issues/68) | `Sfg.Toolkit.DoraMetrics` | `sfg dora` | done — this PR |
| [#77 Reviewer calibration harness](https://github.com/DevelApp-ai/software-field-guide/issues/77) | `Sfg.Toolkit.ReviewCalib` | `sfg review-calib` | done — this PR |
| [#78 Bias probe pack](https://github.com/DevelApp-ai/software-field-guide/issues/78) | `Sfg.Toolkit.BiasProbe` | `sfg bias-probe` | done — this PR |

Related repo: [software-field-guide-contracts](https://github.com/DevelApp-ai/software-field-guide-contracts) — Rego estate/seam contract validator (#57) with a GitHub Pages playground, already delivered as PR #1.

The context mapper draws diagrams in the book's drawio conventions (see the book repo's diagram appendix): `agent="vibe"` on the mxfile, HTML `<br>` labels, house palette of host blue / plugin orange / shared-kernel yellow, green edges for clean dependencies and red dashed edges for forbidden directions.
