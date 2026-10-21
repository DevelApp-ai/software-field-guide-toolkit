using System.Text.Json;

namespace Sfg.Toolkit.TenantProbes;

/// <summary>
/// Configuration for the tenant-isolation probe generator (#58). All keys are
/// optional: the generator infers the target project from the DbContext's
/// nearest project file and auto-detects the DbContext when exactly one
/// exists under the root.
/// </summary>
public sealed class TenantProbeConfig
{
    /// <summary>Path of the target project (csproj), relative to the scanned root. Default: nearest csproj to the DbContext declaration.</summary>
    public string? Project { get; init; }

    /// <summary>Simple name of the DbContext subclass to probe. Default: the single DbContext subclass found under the root.</summary>
    public string? DbContext { get; init; }

    /// <summary>Name of the tenant discriminator property on every probed entity. Default: TenantId.</summary>
    public string TenantProperty { get; init; } = "TenantId";

    /// <summary>Name of the settable tenant-property on the DbContext (session state). Default: CurrentTenantId.</summary>
    public string ContextTenantProperty { get; init; } = "CurrentTenantId";

    /// <summary>First probe tenant identity. Default: tenant-a.</summary>
    public string TenantA { get; init; } = "tenant-a";

    /// <summary>Second probe tenant identity (the cross-tenant adversary). Default: tenant-b.</summary>
    public string TenantB { get; init; } = "tenant-b";

    /// <summary>EF Core database provider package the generated suite references. Default: Microsoft.EntityFrameworkCore.Sqlite.</summary>
    public string ProviderPackage { get; init; } = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>Version of the provider package. Default: 8.0.11.</summary>
    public string ProviderVersion { get; init; } = "8.0.11";

    public static TenantProbeConfig Default { get; } = new();

    public static TenantProbeConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return Default;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new TenantProbeConfig
        {
            Project = ReadString(root, "project"),
            DbContext = ReadString(root, "dbContext"),
            TenantProperty = ReadString(root, "tenantProperty") ?? "TenantId",
            ContextTenantProperty = ReadString(root, "contextTenantProperty") ?? "CurrentTenantId",
            TenantA = ReadString(root, "tenantA") ?? "tenant-a",
            TenantB = ReadString(root, "tenantB") ?? "tenant-b",
            ProviderPackage = ReadString(root, "providerPackage") ?? "Microsoft.EntityFrameworkCore.Sqlite",
            ProviderVersion = ReadString(root, "providerVersion") ?? "8.0.11",
        };
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString()))
        {
            return element.GetString();
        }

        return null;
    }
}
