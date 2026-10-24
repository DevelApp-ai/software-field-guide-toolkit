# MigrationApp — `sfg migration-lint` (#67)

A five-migration EF Core estate that walks the expand → migrate → contract sequence the
book teaches, against the `migration-gates.json` ledger:

| Migration | Date | Kind | What it does |
| --- | --- | --- | --- |
| `20260101100000_InitialCreate` | 2026-01-01 | expand | `Orders` (with `ShippingAddress` and `LegacyTotal`) and `OrderItems` created |
| `20260301100000_AddDeliveryAddress` | 2026-03-01 | expand | the replacement column `Orders.DeliveryAddress` is added — nothing is removed |
| `20260310100000_BackfillDeliveryAddress` | 2026-03-10 | migrate | two batched `UPDATE`s with `WHERE` bounds rewrite rows in chunks |
| `20260315100000_IndexDeliveryAddress` | 2026-03-15 | expand | the new column is indexed for its readers |
| `20260601100000_RemoveShippingAddress` | 2026-06-01 | contract | `Orders.ShippingAddress` is dropped, isolated in its own migration |

The gate pass in `migration-gates.json` is the point of the estate: the removal of
`Orders.ShippingAddress` is *measured*, not dated. The `forward-compat` gate opened when
`DeliveryAddress` went live (`expandedAt` 2026-03-01), the consumers (`ShippingService`,
`LegacyExportJob`) are named, and the old column was observed serving zero reads for seven
consecutive days before `coldAt` 2026-05-25. The drop runs on 2026-06-01 — deliberately
late, after the window closed.

Run it:

```bash
sfg migration-lint demo/MigrationApp --out ml-good
```

The control is silent (`✅ Clean history`) and byte-identical on regeneration. The broken
variants live in [`../broken`](../broken) and CI swaps or overrides them in:

| Variant | How CI seeds it | Rule that bites |
| --- | --- | --- |
| `BackfillDeliveryAddress.Naive.cs` | replaces `20260310100000_BackfillDeliveryAddress.cs` | `migration/naive-backfill` — one `UPDATE` with no `WHERE` |
| `RemoveShippingAddress.DestructiveFirst.cs` | replaces `20260601100000_RemoveShippingAddress.cs` | `migration/destructive-first` + `migration/drop-not-isolated` — the drop references the dropped column to archive it, in the same file |
| `20260602100000_RemoveLegacyTotal.cs` | added to `Migrations/` | `migration/contract-without-gate` — `Orders.LegacyTotal` has no pass |
| `migration-gates.Unmeasured.json` | `--gates` override, no `coldAt` | `migration/removal-without-window` — a pass with no measured window |
| `migration-gates.Rush.json` | `--gates` override, `coldAt` 2026-07-01 | `migration/removal-before-cold` — the drop runs before the column measured cold |

Every bite cites `file:line` evidence — the migration that violates and the ledger pass
it violates against. The control stays silent; each seeded violation is caught by
construction — the #67 done-when.
