using System.Text.Json;
using Sfg.Toolkit.EventHarness;
using Xunit;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// Event contract conformance harness (#60): config validation is loud,
/// the convention scan cites file:line evidence, the generated suite covers
/// every scenario (duplicate, out-of-order, poison, version skew, outbox),
/// and generation is deterministic.
/// </summary>
public sealed class EventHarnessTests : IDisposable
{
    private readonly EventAppFixture _app = new();

    public void Dispose() => _app.Dispose();

    private EventHarnessConfig LoadConfig()
    {
        return EventHarnessConfig.Load(_app.ConfigPath);
    }

    // --- config validation: loud, never a guess -------------------------------

    [Fact]
    public void Config_rejects_a_contract_without_a_consumer()
    {
        _app.Write("bad.json", """
            { "events": [ { "id": "order.placed", "version": 2, "cases": "cases/order.placed.json" } ] }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => EventHarnessConfig.Load(Path.Combine(_app.Root, "bad.json")));
        Assert.Contains("consumer", error.Message);
    }

    [Fact]
    public void Config_rejects_a_contract_without_an_events_array()
    {
        _app.Write("bad.json", """
            { "consumer": "OrdersConsumer" }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => EventHarnessConfig.Load(Path.Combine(_app.Root, "bad.json")));
        Assert.Contains("\"events\" array", error.Message);
    }

    [Fact]
    public void Config_rejects_an_event_without_cases()
    {
        _app.Write("bad.json", """
            { "consumer": "OrdersConsumer", "events": [ { "id": "order.placed", "version": 2 } ] }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => EventHarnessConfig.Load(Path.Combine(_app.Root, "bad.json")));
        Assert.Contains("order.placed", error.Message);
        Assert.Contains("\"cases\"", error.Message);
    }

    [Fact]
    public void Config_rejects_duplicate_event_ids()
    {
        _app.Write("bad.json", """
            {
              "consumer": "OrdersConsumer",
              "events": [
                { "id": "order.placed", "version": 2, "cases": "cases/order.placed.json" },
                { "id": "order.placed", "version": 3, "cases": "cases/order.placed.json" }
              ]
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => EventHarnessConfig.Load(Path.Combine(_app.Root, "bad.json")));
        Assert.Contains("'order.placed' 2 times", error.Message);
    }

    [Fact]
    public void Config_rejects_a_previous_version_that_is_not_older()
    {
        _app.Write("bad.json", """
            {
              "consumer": "OrdersConsumer",
              "events": [ { "id": "order.placed", "version": 2, "cases": "cases/order.placed.json", "previousVersion": 2, "skewCases": "cases/order.placed.v1.json" } ]
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => EventHarnessConfig.Load(Path.Combine(_app.Root, "bad.json")));
        Assert.Contains("not older", error.Message);
    }

    [Fact]
    public void Config_rejects_a_previous_version_without_skew_cases()
    {
        _app.Write("bad.json", """
            {
              "consumer": "OrdersConsumer",
              "events": [ { "id": "order.placed", "version": 2, "cases": "cases/order.placed.json", "previousVersion": 1 } ]
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => EventHarnessConfig.Load(Path.Combine(_app.Root, "bad.json")));
        Assert.Contains("skewCases", error.Message);
    }

    // --- convention scan: evidence, never guesses ------------------------------

    [Fact]
    public void Scan_finds_consumer_and_outbox_with_file_line_evidence()
    {
        var scan = ConventionScanner.Scan(_app.Root, LoadConfig());
        Assert.Equal("OrdersConsumer", scan.Consumer.Name);
        Assert.Equal("Demo.App", scan.Consumer.Namespace);
        Assert.Equal("OrdersConsumer.cs", scan.Consumer.File);
        Assert.Equal(3, scan.Consumer.Line);
        Assert.Contains(scan.ConsumerMembers, m => m.Name == "Handle" && m.Kind == "method");
        Assert.Contains(scan.ConsumerMembers, m => m.Name == "AppliedEffects" && m.Kind == "property");
        Assert.Equal("Outbox", scan.Outbox?.Name);
        Assert.Contains(scan.OutboxMembers, m => m.Name == "MarkDispatched" && m.Kind == "method");
        Assert.Equal("net8.0", scan.TargetFramework);
        Assert.EndsWith("Demo.App.csproj", scan.TargetProject);
    }

    [Fact]
    public void Scan_is_loud_when_the_consumer_class_is_missing()
    {
        _app.Write("ghost.json", """
            { "consumer": "Ghost", "events": [ { "id": "order.placed", "version": 2, "cases": "cases/order.placed.json" } ] }
            """);
        var error = Assert.Throws<InvalidOperationException>(
            () => ConventionScanner.Scan(_app.Root, EventHarnessConfig.Load(Path.Combine(_app.Root, "ghost.json"))));
        Assert.Contains("'Ghost'", error.Message);
        Assert.Contains("no class or record declaration", error.Message);
    }

    [Fact]
    public void Scan_is_loud_when_the_consumer_is_ambiguous()
    {
        _app.Write("Twin.cs", """
            namespace Demo.App.Other;

            public sealed class OrdersConsumer
            {
                public void Handle(string message) { }

                public IReadOnlyList<string> AppliedEffects => Array.Empty<string>();

                public IReadOnlyList<string> DeadLetters => Array.Empty<string>();
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => ConventionScanner.Scan(_app.Root, LoadConfig()));
        Assert.Contains("ambiguous", error.Message);
        Assert.Contains("declared 2 times", error.Message);
        Assert.Contains("OrdersConsumer.cs:3", error.Message);
    }

    [Fact]
    public void Scan_is_loud_when_a_convention_member_is_missing()
    {
        _app.Write("OrdersConsumer.cs", """
            namespace Demo.App;

            public sealed class OrdersConsumer
            {
                public void Handle(string message) { }

                public IReadOnlyList<string> AppliedEffects => Array.Empty<string>();
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => ConventionScanner.Scan(_app.Root, LoadConfig()));
        Assert.Contains("'DeadLetters'", error.Message);
        Assert.Contains("OrdersConsumer.cs:3", error.Message);
        Assert.Contains("missing the convention member(s)", error.Message);
    }

    // --- generator: shape, content, determinism --------------------------------

    private (EventHarnessConfig Config, ConventionScan Scan, EventHarnessSuite Suite) GenerateSuite()
    {
        var config = LoadConfig();
        var scan = ConventionScanner.Scan(_app.Root, config);
        var suite = EventHarnessGenerator.Generate(_app.Root, _app.OutDir, config, scan);
        return (config, scan, suite);
    }

    [Fact]
    public void Generate_produces_the_expected_suite_files()
    {
        var suite = GenerateSuite().Suite;
        var names = suite.Files.Select(f => f.Path).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "Cases.cs", "ConsumerProbes.cs", "EventConformance.csproj", "EventConformanceHarness.cs", "OutboxProbes.cs", "README.md", "manifest.json" },
            names);
    }

    [Fact]
    public void Generate_emits_probes_for_every_scenario_per_event()
    {
        var probes = GenerateSuite().Suite.Files.First(f => f.Path == "ConsumerProbes.cs").Content;
        Assert.Contains("OrderPlaced_duplicate_delivery_applies_exactly_once", probes);
        Assert.Contains("OrderPlaced_out_of_order_arrival_still_applies", probes);
        Assert.Contains("OrderPlaced_poison_routes_to_the_dead_letters_never_crashes", probes);
        Assert.Contains("OrderPlaced_old_schema_replay_is_never_lost", probes);
        Assert.Contains("InvoiceIssued_duplicate_delivery_applies_exactly_once", probes);
        Assert.Contains("InvoiceIssued_poison_routes_to_the_dead_letters_never_crashes", probes);
        Assert.DoesNotContain("InvoiceIssued_old_schema_replay_is_never_lost", probes);
        Assert.Contains("new OrdersConsumer()", probes);
        Assert.Contains("using Demo.App;", probes);
        Assert.Contains("\"order.placed\", 2", probes);
        Assert.Contains("Envelope(id, \"invoice.issued\", 1", probes);
    }

    [Fact]
    public void Generate_embeds_case_payloads_verbatim()
    {
        var cases = GenerateSuite().Suite.Files.First(f => f.Path == "Cases.cs").Content;
        Assert.Contains("public const string OrderPlaced = \"\"\"", cases);
        Assert.Contains("o-1001", cases);
        Assert.Contains("public const string OrderPlacedSkew = \"\"\"", cases);
        Assert.Contains("o-0042", cases);
        Assert.Contains("public const string InvoiceIssued = \"\"\"", cases);
        Assert.Contains("i-5001", cases);
    }

    [Fact]
    public void Generate_emits_outbox_health_probes_when_an_outbox_is_declared()
    {
        var outbox = GenerateSuite().Suite.Files.First(f => f.Path == "OutboxProbes.cs").Content;
        Assert.Contains("Published_messages_wait_in_the_pending_queue", outbox);
        Assert.Contains("Double_dispatch_is_idempotent", outbox);
        Assert.Contains("new Outbox()", outbox);
        Assert.Contains("Assert.Empty(outbox.Pending);", outbox);
    }

    [Fact]
    public void Generate_skips_outbox_probes_when_no_outbox_is_declared()
    {
        _app.Write("events.json", """
            {
              "consumer": "OrdersConsumer",
              "events": [ { "id": "order.placed", "version": 2, "cases": "cases/order.placed.json" } ]
            }
            """);
        var suite = GenerateSuite().Suite;
        Assert.False(suite.HasOutbox);
        Assert.DoesNotContain(suite.Files, f => f.Path == "OutboxProbes.cs");
        Assert.Contains("\"outbox\": null", suite.Files.First(f => f.Path == "manifest.json").Content);
    }

    [Fact]
    public void Generate_is_loud_when_a_cases_file_is_missing()
    {
        _app.Write("events.json", """
            {
              "consumer": "OrdersConsumer",
              "events": [ { "id": "order.placed", "version": 2, "cases": "cases/missing.json" } ]
            }
            """);
        var error = Assert.Throws<InvalidOperationException>(() => GenerateSuite());
        Assert.Contains("'order.placed'", error.Message);
        Assert.Contains("cases/missing.json", error.Message);
    }

    [Fact]
    public void Manifest_records_generator_evidence_and_case_counts()
    {
        var manifest = GenerateSuite().Suite.Files.First(f => f.Path == "manifest.json").Content;
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        Assert.Equal("sfg events 0.5.0", root.GetProperty("generator").GetString());
        Assert.Equal("OrdersConsumer", root.GetProperty("consumer").GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("consumer").GetProperty("line").GetInt32());
        Assert.Equal("Outbox", root.GetProperty("outbox").GetProperty("name").GetString());
        Assert.Equal("net8.0", root.GetProperty("targetFramework").GetString());
        Assert.Equal(
            new[] { "duplicate-delivery", "out-of-order-arrival", "poison-payloads", "version-skew" },
            root.GetProperty("scenarios").EnumerateArray().Select(s => s.GetString()));
        var events = root.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("invoice.issued", events[0].GetProperty("id").GetString());
        Assert.Equal(1, events[0].GetProperty("caseCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("previousVersion").ValueKind);
        Assert.Equal("order.placed", events[1].GetProperty("id").GetString());
        Assert.Equal(2, events[1].GetProperty("caseCount").GetInt32());
        Assert.Equal(1, events[1].GetProperty("previousVersion").GetInt32());
        Assert.Equal(1, events[1].GetProperty("skewCaseCount").GetInt32());
    }

    [Fact]
    public void Generation_is_deterministic_byte_identical()
    {
        var first = GenerateSuite().Suite;
        var second = GenerateSuite().Suite;
        Assert.Equal(first.Files.Count, second.Files.Count);
        for (var i = 0; i < first.Files.Count; i++)
        {
            Assert.Equal(first.Files[i].Path, second.Files[i].Path);
            Assert.Equal(first.Files[i].Content, second.Files[i].Content);
        }
    }

    // --- renderer: the board ----------------------------------------------------

    [Fact]
    public void Board_cites_consumer_evidence_and_lists_every_event()
    {
        var (config, scan, suite) = GenerateSuite();
        var board = EventHarnessRenderer.Markdown(config, scan, suite);
        Assert.Contains("# Event contract conformance board", board);
        Assert.Contains("`order.placed`", board);
        Assert.Contains("`invoice.issued`", board);
        Assert.Contains("`OrdersConsumer.cs:3`", board);
        Assert.Contains("1 skew cases", board);
        Assert.Contains("Summary: 2 events, 7 consumer probes, 2 outbox probes", board);
    }

    [Fact]
    public void Board_json_is_valid_and_deterministic()
    {
        var (config, scan, suite) = GenerateSuite();
        var json = EventHarnessRenderer.Json(config, scan, suite);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("sfg events 0.5.0", document.RootElement.GetProperty("generator").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("events").GetArrayLength());
        Assert.Equal(json, EventHarnessRenderer.Json(config, scan, GenerateSuite().Suite));
    }
}
