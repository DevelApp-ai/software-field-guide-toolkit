namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic strangler-fig application: a routing pivot with three wired
/// routes plus one non-literal wiring call the scanner must skip, legacy
/// and new handlers, two shadow case files, and a route table whose
/// strangled route has retired below the threshold — the sunset tripwire's
/// baseline fire. Variants seed the failure modes (unwired routes,
/// undeclared wires, lying traffic, missing/ambiguous handlers).
/// </summary>
public sealed class StranglerAppFixture : IDisposable
{
    public StranglerAppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "migration.json");

        Write("Demo.App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("MigrationPivot.cs", """
            namespace Demo.App;

            public sealed class MigrationPivot
            {
                public void MapRoute(string id) { }
            }
            """);
        Write("RouteTableSetup.cs", """
            namespace Demo.App;

            public static class RouteTableSetup
            {
                public static void Wire(MigrationPivot pivot)
                {
                    pivot.MapRoute("orders.search");
                    pivot.MapRoute("orders.get");
                    pivot.MapRoute("shipping.quote");
                    var method = "promo.code";
                    pivot.MapRoute(method);
                }
            }
            """);
        Write("Legacy/LegacyOrdersSearch.cs", """
            namespace Demo.App.Legacy;

            public sealed class LegacyOrdersSearch
            {
                public string Handle(string input) => input;
            }
            """);
        Write("Legacy/LegacyOrdersGet.cs", """
            namespace Demo.App.Legacy;

            public sealed class LegacyOrdersGet
            {
                public string Handle(string input) => input;
            }
            """);
        Write("Legacy/LegacyShippingQuote.cs", """
            namespace Demo.App.Legacy;

            public sealed class LegacyShippingQuote
            {
                public string Handle(string input) => input;
            }
            """);
        Write("New/OrdersSearchService.cs", """
            namespace Demo.App.New;

            public sealed class OrdersSearchService
            {
                public string Handle(string input) => input;
            }
            """);
        Write("New/ShippingQuoteService.cs", """
            namespace Demo.App.New;

            public sealed class ShippingQuoteService
            {
                public string Handle(string input) => input;
            }
            """);
        Write("cases/orders.search.json", """
            [
              { "sku": "A-1001", "qty": 2 },
              { "sku": "B-2002", "qty": 1 }
            ]
            """);
        Write("cases/shipping.quote.json", """
            [
              { "weightKg": 120.5, "destination": "DK" }
            ]
            """);
        WriteTable("migration.json", BaselineTable());
    }

    public string Root { get; }

    public string ConfigPath { get; }

    public string SeedUnwiredTable()
    {
        var baseline = BaselineTable().Replace(
            "    {\n      \"id\": \"shipping.quote\",",
            "    {\n      \"id\": \"promo.code\",\n      \"state\": \"strangling\",\n      \"old\": \"LegacyPromoCodes\",\n      \"new\": \"PromoCodeService\"\n    },\n    {\n      \"id\": \"shipping.quote\",");
        return WriteTable("migration.unwired.json", baseline);
    }

    public void SeedUndeclaredWire()
    {
        Write("RouteTableExtra.cs", """
            namespace Demo.App;

            public static class RouteTableExtra
            {
                public static void Wire(MigrationPivot pivot)
                {
                    pivot.MapRoute("admin.audit");
                }
            }
            """);
    }

    public string SeedLyingTable()
    {
        return WriteTable(
            "migration.lying.json",
            BaselineTable().Replace("[ 900, 40, 0, 0, 0 ]", "[ 900, 400, 350, 280, 240 ]"));
    }

    public string SeedInsufficientHistoryTable()
    {
        return WriteTable(
            "migration.short.json",
            BaselineTable().Replace("[ 900, 40, 0, 0, 0 ]", "[ 900, 40 ]"));
    }

    public string SeedStranglingTrafficTable()
    {
        return WriteTable(
            "migration.strangling.json",
            BaselineTable()
                .Replace("\"state\": \"strangled\"", "\"state\": \"strangling\""));
    }

    public string SeedMissingHandlerTable()
    {
        return WriteTable(
            "migration.missing-handler.json",
            BaselineTable().Replace("\"new\": \"OrdersSearchService\"", "\"new\": \"NoSuchService\""));
    }

    public string SeedMissingCasesTable()
    {
        return WriteTable(
            "migration.missing-cases.json",
            BaselineTable().Replace("\"cases\": \"cases/orders.search.json\"", "\"cases\": \"cases/missing.json\""));
    }

    public void SeedAmbiguousNewHandler()
    {
        Write("New/OrdersSearchService.Copy.cs", """
            namespace Demo.App.New;

            public sealed class OrdersSearchService
            {
                public string Handle(string input) => input;
            }
            """);
    }

    public string WriteTable(string relativePath, string json)
    {
        Write(relativePath, json);
        return Path.Combine(Root, relativePath);
    }

    public int LineOf(string relativePath, string needle)
    {
        var lines = File.ReadAllLines(Path.Combine(Root, relativePath));
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle))
            {
                return i + 1;
            }
        }

        throw new ArgumentException($"needle '{needle}' not found in {relativePath}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort on Windows tempfile contention
        }
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    private string BaselineTable() => """
        {
          "routes": [
            {
              "id": "orders.search",
              "state": "strangling",
              "old": "LegacyOrdersSearch",
              "new": "OrdersSearchService",
              "shadow": true,
              "cases": "cases/orders.search.json",
              "tolerance": { "numeric": { "total": 0.01 }, "ignore": [ "servedBy" ] },
              "traffic": [ 500, 400 ]
            },
            {
              "id": "orders.get",
              "state": "todo",
              "old": "LegacyOrdersGet"
            },
            {
              "id": "shipping.quote",
              "state": "strangled",
              "old": "LegacyShippingQuote",
              "new": "ShippingQuoteService",
              "shadow": true,
              "cases": "cases/shipping.quote.json",
              "tolerance": { "numeric": { "quote": 0.01 }, "ignore": [ "servedBy" ] },
              "traffic": [ 900, 40, 0, 0, 0 ],
              "retirementThreshold": 100,
              "sunsetPeriods": 3
            }
          ]
        }
        """;
}
