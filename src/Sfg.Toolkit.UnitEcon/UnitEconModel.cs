using System.Globalization;
using System.Text.Json;

namespace Sfg.Toolkit.UnitEcon;

/// <summary>
/// One line of the recorded billing export: a service's cost for one period.
/// Amounts are in the config's declared currency; the period is a date whose
/// bucket depends on the configured granularity.
/// </summary>
public sealed record BillingLine
{
    public required string Service { get; init; }

    public required DateTimeOffset Period { get; init; }

    public required double Amount { get; init; }

    /// <summary>1-based line of this row in the billing file, for citations.</summary>
    public int Line { get; init; }

    public string Anchor(string fileName) => $"{fileName}:{Line}";
}

/// <summary>The recorded billing export: an export id, an optional currency
/// marker that must match the config's, and the billed line items.</summary>
public sealed record BillingExport
{
    public string Export { get; init; } = string.Empty;

    public IReadOnlyList<BillingLine> Lines { get; init; } = Array.Empty<BillingLine>();
}

/// <summary>
/// One allocation rule: the share of a service's cost that belongs to a
/// tenant. Shares are fractions of one; a service's rules should sum to 1.0
/// (the extractor verifies this — #68 discipline).
/// </summary>
public sealed record AllocationRule
{
    public required string Tenant { get; init; }

    public required string Service { get; init; }

    public required double Share { get; init; }

    /// <summary>1-based line of this row in the allocations file, for citations.</summary>
    public int Line { get; init; }

    public string Anchor(string fileName) => $"{fileName}:{Line}";
}

/// <summary>The per-tenant allocation manifest: a manifest id and the rules.</summary>
public sealed record AllocationManifest
{
    public string Manifest { get; init; } = string.Empty;

    public IReadOnlyList<AllocationRule> Rules { get; init; } = Array.Empty<AllocationRule>();
}

/// <summary>
/// One recorded request count: a tenant's requests against a service on a
/// date. Rows are additive — a day may be recorded as several samples.
/// </summary>
public sealed record TelemetryRow
{
    public required string Tenant { get; init; }

    public required string Service { get; init; }

    public required DateTimeOffset Date { get; init; }

    public required long Requests { get; init; }

    /// <summary>1-based line of this row in the telemetry file, for citations.</summary>
    public int Line { get; init; }

    public string Anchor(string fileName) => $"{fileName}:{Line}";
}

/// <summary>The recorded request telemetry: a source id and the request rows.</summary>
public sealed record TelemetryExport
{
    public string Source { get; init; } = string.Empty;

    public IReadOnlyList<TelemetryRow> Rows { get; init; } = Array.Empty<TelemetryRow>();
}

/// <summary>Severity-ordered finding: errors before warnings, then rule, then message.</summary>
public sealed record UnitEconFinding(string Rule, string Severity, string Message, IReadOnlyList<string> Evidence)
    : IComparable<UnitEconFinding>
{
    public int CompareTo(UnitEconFinding? other)
    {
        if (other is null)
        {
            return 1;
        }

        var severityRank = Severity == "error" ? 0 : 1;
        var otherRank = other.Severity == "error" ? 0 : 1;
        var severity = severityRank.CompareTo(otherRank);
        if (severity != 0)
        {
            return severity;
        }

        var rule = string.CompareOrdinal(Rule, other.Rule);
        if (rule != 0)
        {
            return rule;
        }

        return string.CompareOrdinal(Message, other.Message);
    }
}

/// <summary>Per-tenant spend for one period against its declared budget.</summary>
public sealed record SpendRow(
    string Tenant,
    string Period,
    double Spend,
    double? Budget,
    string Status);

/// <summary>One cost-per-request trend cell: a tenant's allocated cost for a
/// service in a period, the requests it served, and the unit price.</summary>
public sealed record CostPerRequestRow(
    string Tenant,
    string Service,
    string Period,
    double AllocatedCost,
    long Requests,
    double? CostPerRequest);

/// <summary>The analyzed unit-economics report, deterministic for given exports.</summary>
public sealed record UnitEconReport
{
    public required string Estate { get; init; }

    public required string Export { get; init; }

    public required string Manifest { get; init; }

    public required string Source { get; init; }

    public int Tenants { get; init; }

    public int Services { get; init; }

    public int Periods { get; init; }

    public required IReadOnlyList<SpendRow> Spend { get; init; }

    public required IReadOnlyList<CostPerRequestRow> CostPerRequest { get; init; }

    /// <summary>Every finding, severity-ordered (before the cap).</summary>
    public required IReadOnlyList<UnitEconFinding> Findings { get; init; }

    /// <summary>The findings actually rendered, capped at the config limit.</summary>
    public required IReadOnlyList<UnitEconFinding> RenderedFindings { get; init; }

    public int Errors { get; init; }

    public int Warnings { get; init; }

    public int Cap { get; init; }
}

/// <summary>
/// Loads the recorded billing export, the per-tenant allocation manifest, and
/// the request telemetry with loud errors — malformed exports never pass
/// silently. Every parsed row carries its 1-based source line so every
/// finding cites file:line evidence.
/// </summary>
public static class UnitEconFiles
{
    public static BillingExport LoadBilling(string path, string currency)
    {
        var text = ReadText(path, "billing export");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"billing export is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("billing export must be a JSON object with a \"lineItems\" array");
            }

            var export = ReadString(root, "export") ?? throw new InvalidOperationException(
                "billing export needs a non-empty \"export\" — the recorded export id");
            if (string.IsNullOrWhiteSpace(export))
            {
                throw new InvalidOperationException("billing export \"export\" must be non-empty");
            }

            if (root.TryGetProperty("currency", out var currencyElement))
            {
                var billedCurrency = currencyElement.ValueKind == JsonValueKind.String ? currencyElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(billedCurrency))
                {
                    throw new InvalidOperationException(
                        "billing export \"currency\" must be a non-empty string when present");
                }

                if (!string.Equals(billedCurrency, currency, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"billing export is billed in {billedCurrency} but the config declares {currency} — a unit-economics join must not cross currencies silently");
                }
            }

            if (!root.TryGetProperty("lineItems", out var linesElement) ||
                linesElement.ValueKind != JsonValueKind.Array || linesElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "billing export needs a non-empty \"lineItems\" array — a service's cost per period");
            }

            var lines = new List<BillingLine>();
            var cursor = 0;
            foreach (var entry in linesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every billing line item must be a JSON object");
                }

                var service = RequireString(entry, "service", "billing line item");
                var line = LineOf(text, $"\"service\": \"{service}\"", ref cursor);

                var periodText = RequireString(entry, "period", "billing line item");
                if (!TryReadDate(periodText, out var period))
                {
                    throw new InvalidOperationException(
                        $"billing line item for '{service}' has a malformed \"period\": {periodText}");
                }

                if (!entry.TryGetProperty("amount", out var amountElement) ||
                    amountElement.ValueKind != JsonValueKind.Number || amountElement.GetDouble() <= 0)
                {
                    throw new InvalidOperationException(
                        $"billing line item for '{service}' needs a positive \"amount\" — zero-cost lines are not billable truth");
                }

                lines.Add(new BillingLine
                {
                    Service = service,
                    Period = period,
                    Amount = amountElement.GetDouble(),
                    Line = line,
                });
            }

            return new BillingExport
            {
                Export = export,
                Lines = lines.ToArray(),
            };
        }
    }

    public static AllocationManifest LoadAllocations(string path)
    {
        var text = ReadText(path, "allocation manifest");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"allocation manifest is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("allocation manifest must be a JSON object with a \"rules\" array");
            }

            var manifest = ReadString(root, "manifest") ?? throw new InvalidOperationException(
                "allocation manifest needs a non-empty \"manifest\" — the manifest id");
            if (string.IsNullOrWhiteSpace(manifest))
            {
                throw new InvalidOperationException("allocation manifest \"manifest\" must be non-empty");
            }

            if (!root.TryGetProperty("rules", out var rulesElement) ||
                rulesElement.ValueKind != JsonValueKind.Array || rulesElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "allocation manifest needs a non-empty \"rules\" array — which tenant owns which share of which service");
            }

            var rules = new List<AllocationRule>();
            var cursor = 0;
            foreach (var entry in rulesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every allocation rule must be a JSON object");
                }

                var tenant = RequireString(entry, "tenant", "allocation rule");
                var service = RequireString(entry, "service", "allocation rule");
                var line = LineOf(text, $"\"service\": \"{service}\"", ref cursor);

                if (!entry.TryGetProperty("share", out var shareElement) ||
                    shareElement.ValueKind != JsonValueKind.Number ||
                    shareElement.GetDouble() <= 0 || shareElement.GetDouble() > 1)
                {
                    throw new InvalidOperationException(
                        $"allocation rule for '{tenant}' on '{service}' needs a \"share\" between 0 and 1 — shares are fractions of one");
                }

                rules.Add(new AllocationRule
                {
                    Tenant = tenant,
                    Service = service,
                    Share = shareElement.GetDouble(),
                    Line = line,
                });
            }

            foreach (var group in rules.GroupBy(r => (r.Tenant, r.Service)))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate allocation rule for '{group.Key.Tenant}' on '{group.Key.Service}' — combine the shares deliberately, one rule per tenant and service");
                }
            }

            return new AllocationManifest
            {
                Manifest = manifest,
                Rules = rules.ToArray(),
            };
        }
    }

    public static TelemetryExport LoadTelemetry(string path)
    {
        var text = ReadText(path, "request telemetry");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"request telemetry is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("request telemetry must be a JSON object with a \"requests\" array");
            }

            var source = ReadString(root, "source") ?? throw new InvalidOperationException(
                "request telemetry needs a non-empty \"source\" — the recorded telemetry id");
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException("request telemetry \"source\" must be non-empty");
            }

            if (!root.TryGetProperty("requests", out var rowsElement) ||
                rowsElement.ValueKind != JsonValueKind.Array || rowsElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "request telemetry needs a non-empty \"requests\" array — the recorded request counts to price");
            }

            var rows = new List<TelemetryRow>();
            var cursor = 0;
            foreach (var entry in rowsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("every telemetry row must be a JSON object");
                }

                var tenant = RequireString(entry, "tenant", "telemetry row");
                var service = RequireString(entry, "service", "telemetry row");
                var line = LineOf(text, $"\"service\": \"{service}\"", ref cursor);

                var dateText = RequireString(entry, "date", "telemetry row");
                if (!TryReadDate(dateText, out var date))
                {
                    throw new InvalidOperationException(
                        $"telemetry row for '{tenant}' on '{service}' has a malformed \"date\": {dateText}");
                }

                if (!entry.TryGetProperty("requests", out var requestsElement) ||
                    requestsElement.ValueKind != JsonValueKind.Number ||
                    requestsElement.TryGetInt64(out var requests) == false || requests <= 0)
                {
                    throw new InvalidOperationException(
                        $"telemetry row for '{tenant}' on '{service}' needs a positive integer \"requests\" count");
                }

                rows.Add(new TelemetryRow
                {
                    Tenant = tenant,
                    Service = service,
                    Date = date,
                    Requests = requests,
                    Line = line,
                });
            }

            return new TelemetryExport
            {
                Source = source,
                Rows = rows.ToArray(),
            };
        }
    }

    private static string ReadText(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{what} not found: {path}");
        }

        return File.ReadAllText(path);
    }

    /// <summary>1-based line of the next occurrence of the needle at or after the cursor; the cursor advances.</summary>
    private static int LineOf(string text, string needle, ref int cursor)
    {
        var index = text.IndexOf(needle, cursor, StringComparison.Ordinal);
        if (index < 0)
        {
            index = Math.Max(0, cursor);
        }

        cursor = index + needle.Length;
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool TryReadDate(string value, out DateTimeOffset date)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            date = parsed;
            return true;
        }

        date = default;
        return false;
    }

    private static string RequireString(JsonElement entry, string name, string what)
    {
        if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidOperationException($"every {what} needs a non-empty \"{name}\"");
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }
}
