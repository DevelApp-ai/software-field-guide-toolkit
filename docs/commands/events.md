# sfg events

> Book issue #60 Event contract conformance harness.

Given a message contract (`events.json`), generates a compilable xunit suite that replays chaos-lite against the real consumer: duplicate delivery (exactly-once effects), out-of-order arrival, poison payloads (routed to the dead letters, never a crash), and old-schema producer replay (version-skew tolerance), plus outbox health checks on the producer side (publish waits pending; double dispatch stays idempotent). Seeded faults visibly fail a naive consumer and pass a compliant one — the #60 done-when.

## Usage

```bash
# event contract conformance suite: chaos-lite replay against the real consumer
sfg events /path/to/app --out events
# events also accepts --config events.json (see below); it defaults to
#   <root>/events.json
# common flags (--config, --out, --strict): see the Getting started page
```

## Outputs

`events` writes the conformance board and a compilable xunit project (run it with `dotnet test`):

- `events.md` — the human board: every event, its version, case counts, old-schema replay, the scenarios replayed per event, and the conventions verified with `file:line` evidence.
- `events.json` — the machine summary: consumer and outbox declarations, target project, events with case counts.
- `EventConformance.csproj` + `EventConformanceHarness.cs` + `ConsumerProbes.cs` + `OutboxProbes.cs` + `Cases.cs` — per event: duplicate delivery applies the message id exactly once; reversed (out-of-order) delivery still applies every case; a fixed poison corpus (malformed JSON, a non-object message, a type-violating envelope) routes to the dead letters without crashing; and for events declaring a `previousVersion`, old-schema replays are applied or dead-lettered — never lost. With an outbox declared: a publish waits in `Pending`, and a double `MarkDispatched` leaves exactly one `Dispatched` entry and an empty `Pending`. Failures carry `EVENT CONTRACT violated on '...'`, naming the event, the message id, and the broken guarantee.
- `manifest.json` — the versioned inventory: consumer and outbox with member evidence, scenarios, per-event case counts.
- `README.md` — what each probe bites on, per event.

## Inputs

## Event contract (`events.json`, the events harness's required input)

```json
{
  "project": "src/MyApp/MyApp.csproj",
  "consumer": "OrderEventsConsumer",
  "outbox": "Outbox",
  "events": [
    {
      "id": "order.placed",
      "version": 2,
      "cases": "cases/order.placed.json",
      "previousVersion": 1,
      "skewCases": "cases/order.placed.v1.json"
    },
    {
      "id": "invoice.issued",
      "version": 1,
      "cases": "cases/invoice.issued.json"
    }
  ]
}
```

`cases` (and `skewCases`, required when `previousVersion` is declared) are JSON files of payload arrays, replayed verbatim. `previousVersion` must be older than `version` — the version-skew scenario replays an old-schema producer, never a guess about ordering. Duplicate event ids, missing case files, and skew without a previous version fail loudly at generation time.

## Conventions the events target must satisfy

- the consumer exposes `Handle(string message)` — one delivery, one call,
- the consumer exposes `AppliedEffects` (`IReadOnlyList<string>` of applied message ids, deduplicated — at-least-once delivery, exactly-once effects),
- the consumer exposes `DeadLetters` (`IReadOnlyList<string>` of raw dead-lettered messages),
- the consumer has a parameterless constructor;
- the outbox (optional) exposes `Publish(string)`, `MarkDispatched(string)`, `Pending` and `Dispatched` (`IReadOnlyList<string>`), and a parameterless constructor.

If a convention is missing the generator fails loudly with file:line evidence — never a silent skip.

## Honest status

- **`events` proves tolerance on replayed cases, not production behavior.** The convention scan is syntax-level: the consumer and outbox resolve by simple class/record name (missing or ambiguous declarations are loud errors). The probes replay deterministic envelopes — ids `case-N`, `skew-N`, `ob-N` — against freshly constructed consumers; they prove the tolerance mechanisms (dedup, dead-lettering, skew handling, outbox idempotency) exist and work on the declared cases, not that every production path is safe. Poison payloads are a fixed corpus (malformed JSON, a non-object message, a type-violating envelope); extend the corpus by adding cases, not by editing generated files.

## In-repo demo

The events demo is in-repo too: [`demo/EventApp`](https://github.com/DevelApp-ai/software-field-guide-toolkit/blob/main/demo/README.md), a small eventing app with a compliant `OrderEventsConsumer` (dedup, dead letters, old-schema tolerance) and `Outbox`, two event contracts — `order.placed` v2 with a v1 skew replay, and `invoice.issued` v1 — plus two deliberately regressed variants: a naive consumer (no dedup, crashes on poison, refuses old versions) and a fire-and-forget outbox (publishes vanish, dispatches leave no bookkeeping). CI generates the suite, runs it green against the good app, then swaps each broken variant in and asserts the suite **fails** with `EventConformance.ConsumerProbes` / `EventConformance.OutboxProbes` — the #60 done-when: seeded faults visibly fail a naive consumer and pass a compliant one.
