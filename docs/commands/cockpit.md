# sfg cockpit

> Book issue #56 Strangler-fig migration cockpit.

Reads the migration route table (`migration.json`), checks it against the routing seam (every declared route wired; no undeclared wires), runs the sunset tripwire (a strangled route whose old-implementation traffic stayed under the retirement threshold for the full sunset window — or the fig is not finished), and generates a compilable shadow-diff xunit suite comparing old and new implementations per declared tolerance.

## Usage

```bash
# strangler-fig cockpit: route board + sunset tripwire + shadow-diff suite
sfg cockpit /path/to/app --out cockpit
# cockpit also accepts --config migration.json (see below); it defaults to
#   <root>/migration.json when the file exists
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`cockpit` writes the migration board and, for routes with `"shadow": true`, a compilable shadow-diff suite:

- `cockpit.md` — the human board: every route's state, wiring evidence, old/new handlers, traffic history, retirement threshold, and sunset verdict; findings sorted errors-first with `file:line` citations.
- `cockpit.json` — the machine summary: routes, wiring, tripwire verdicts, and findings.
- `cockpit.drawio` — the state map in the book's conventions (strangled green, strangling amber, todo grey; a fired tripwire draws a red dashed stroke).
- `ShadowDiff.csproj` + `ShadowDiffHarness.cs` + `RouteShadowProbes.cs` + `Cases.cs` — one `[Fact]` per shadow route: the same cases through old and new, deep JSON comparison with per-route tolerance (numeric fields keyed by leaf name at any depth; ignored fields skipped at any depth). A drift fails with `SHADOW DRIFT on route '...' case #...`, naming the route, case, path, and both values. Run with `dotnet test`.
- `manifest.json` — the shadow inventory: target project, per-route old/new handlers with file:line, case counts, tolerance echo.
- `README.md` — what the suite bites on, per route.

## Inputs

## Route table (`migration.json`, the cockpit's required input)

```json
{
  "project": "src/MyWeb/MyWeb.csproj",
  "routeMethod": "MapRoute",
  "defaultRetirementThreshold": 100,
  "defaultSunsetPeriods": 3,
  "routes": [
    {
      "id": "orders.search",
      "state": "strangling",
      "old": "LegacyOrdersSearch",
      "new": "OrdersSearchService",
      "shadow": true,
      "cases": "cases/orders.search.json",
      "tolerance": { "numeric": { "total": 0.01 }, "ignore": [ "servedBy", "elapsedMs" ] },
      "traffic": [ 4200, 3100, 650 ]
    },
    {
      "id": "shipping.quote",
      "state": "strangled",
      "old": "LegacyShippingQuote",
      "new": "ShippingQuoteService",
      "traffic": [ 4200, 210, 90, 0, 0, 0 ],
      "retirementThreshold": 100,
      "sunsetPeriods": 3
    }
  ]
}
```

`state` is `todo`, `strangling`, or `strangled` (strangling/strangled need `old` + `new`; shadow routes need `old` + `new` + `cases`). `traffic` is the old implementation's request count per period, oldest first. Only `routes` is required; `project` is optional (defaults to the shadow handlers' single distinct project), `routeMethod` defaults to `MapRoute`. The pivot scan honors only string-literal route ids — dynamic ids are skipped loudly by omission, never guessed.

## Rules

| Rule | Severity | Fires when |
| --- | --- | --- |
| `migration/unwired-route` | error | a declared route is never wired at the routing seam — the pivot must call `MapRoute("id", ...)` before the migration can claim it |
| `migration/undeclared-route` | warning | a route is wired at the seam but missing from the route table — an undeclared route strangles nothing |
| `migration/sunset-window` | warning | tripwire: a strangled route's old-implementation traffic stayed at or under the retirement threshold for the full sunset window — delete the old handler and remove the route entry |
| `migration/strangle-unfinished` | error | tripwire: a route is declared strangled but old-implementation traffic still exceeds the retirement threshold in the sunset window — the fig has not strangled it |
Traffic history shorter than the sunset window keeps both tripwires silent — no verdict, no guess.

## Honest status

- **`cockpit` reasons about claims, not proof of traffic.** The pivot and handler scans are syntax-level: wiring evidence is a string-literal route id (dynamic ids are skipped, never guessed), handler resolution is by simple class/record name (missing or ambiguous declarations are loud errors). The shadow-diff suite exercises the declared `old`/`new` handlers under the convention `string <Method>(string input)` with JSON in and JSON out; it proves the two implementations agree on the declared cases within the declared tolerance — it does not observe production traffic. The `traffic` history is data you attest to in the route table; the tripwires check it for consistency, not provenance.

## In-repo demo

The cockpit demo is also in-repo: [`demo/StranglerApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a small strangler-fig app whose `shipping.quote` route is strangled end to end — promoted at the routing pivot, shadow-diffed green against the retired legacy handler, and retired below the traffic threshold, so the sunset tripwire fires (the #56 done-when: one route strangled with the tripwire armed and firing). CI also runs three deliberately broken variants: a drifted new implementation (the shadow suite fails with `SHADOW DRIFT`), an unwired declared route (`migration/unwired-route` error), and a strangled route whose traffic contradicts the claim (`migration/strangle-unfinished` error).
