namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic eventing application (#60): a compliant consumer with the
/// harness conventions (Handle, AppliedEffects, DeadLetters), a compliant
/// outbox (Publish, MarkDispatched, Pending, Dispatched), two event
/// contracts — one with an old-schema replay — and their case files.
/// Variants seed the failure modes (missing convention members, ambiguous
/// declarations, missing cases files, naive consumers).
/// </summary>
public sealed class EventAppFixture : IDisposable
{
    public EventAppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-events-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "events.json");

        Write("Demo.App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("OrdersConsumer.cs", """
            namespace Demo.App;

            public sealed class OrdersConsumer
            {
                private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
                private readonly List<string> _effects = new();
                private readonly List<string> _deadLetters = new();

                public IReadOnlyList<string> AppliedEffects => _effects;

                public IReadOnlyList<string> DeadLetters => _deadLetters;

                public void Handle(string message)
                {
                    _applied.Add(message);
                    _effects.Add(message);
                }
            }
            """);
        Write("Outbox.cs", """
            namespace Demo.App;

            public sealed class Outbox
            {
                private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
                private readonly HashSet<string> _dispatched = new(StringComparer.Ordinal);

                public IReadOnlyList<string> Pending => _pending.ToList();

                public IReadOnlyList<string> Dispatched => _dispatched.ToList();

                public void Publish(string messageId) => _pending.Add(messageId);

                public void MarkDispatched(string messageId)
                {
                    if (_pending.Remove(messageId))
                    {
                        _dispatched.Add(messageId);
                    }
                }
            }
            """);
        Write("cases/order.placed.json", """
            [
              { "orderId": "o-1001", "total": 129.90 },
              { "orderId": "o-1002", "total": 89.50 }
            ]
            """);
        Write("cases/order.placed.v1.json", """
            [
              { "orderId": "o-0042", "amount": "129.90" }
            ]
            """);
        Write("cases/invoice.issued.json", """
            [
              { "invoiceId": "i-5001", "amount": 129.90 }
            ]
            """);
        Write("events.json", """
            {
              "consumer": "OrdersConsumer",
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
            """);
    }

    public string Root { get; }

    public string ConfigPath { get; }

    public string OutDir => Path.Combine(Root, "out");

    public string RerunDir => Path.Combine(Root, "rerun");

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
