using System.Text.Json;

namespace Sfg.Toolkit.Core.CodeTruth;

public sealed class SfgConfig
{
    public IReadOnlyList<string> ContractNamespaces { get; init; } = new[] { ".Contracts" };

    public IReadOnlyList<string> SharedKernelProjects { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenEdges { get; init; } = new[]
    {
        "plugin->plugin",
        "shared-kernel->plugin",
        "shared-kernel->host",
    };

    public IReadOnlyList<string> PluginContractInterfaces { get; init; } = new[] { "IPluginClass", "IPluginModule" };

    public IReadOnlyList<string> EventContracts { get; init; } = new[] { "IEventHandler" };

    /// <summary>Explicit role overrides, keyed by project name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>();

    public static SfgConfig Default { get; } = new();

    public static SfgConfig Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return Default;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var config = new SfgConfig
        {
            ContractNamespaces = ReadArray(root, "contractNamespaces") ?? new[] { ".Contracts" },
            SharedKernelProjects = ReadArray(root, "sharedKernelProjects") ?? Array.Empty<string>(),
            ForbiddenEdges = ReadArray(root, "forbiddenEdges") ?? new[]
            {
                "plugin->plugin",
                "shared-kernel->plugin",
                "shared-kernel->host",
            },
            PluginContractInterfaces = ReadArray(root, "pluginContractInterfaces") ?? new[] { "IPluginClass", "IPluginModule" },
            EventContracts = ReadArray(root, "eventContracts") ?? new[] { "IEventHandler" },
            Roles = ReadRoles(root),
        };
        return config;
    }

    private static IReadOnlyDictionary<string, string> ReadRoles(JsonElement root)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("roles", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    roles[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }

        return roles;
    }

    private static IReadOnlyList<string>? ReadArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return element.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToArray();
    }

    public ContextRole RoleOf(string projectName)
    {
        if (Roles.TryGetValue(projectName, out var roleText) && ParseRole(roleText) is { } role)
        {
            return role;
        }

        foreach (var name in SharedKernelProjects)
        {
            if (string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase))
            {
                return ContextRole.SharedKernel;
            }
        }

        if (projectName.Contains("SharedKernel", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Shared", StringComparison.OrdinalIgnoreCase))
        {
            return ContextRole.SharedKernel;
        }

        // Test projects are not boundary participants: consuming the code under
        // test is their purpose, so they never trigger direction rules.
        if (projectName.Contains("Test", StringComparison.OrdinalIgnoreCase))
        {
            return ContextRole.Other;
        }

        if (projectName.Contains("Plugin", StringComparison.OrdinalIgnoreCase))
        {
            return ContextRole.Plugin;
        }

        if (projectName.Contains("Host", StringComparison.OrdinalIgnoreCase))
        {
            return ContextRole.Host;
        }

        return ContextRole.Other;
    }

    public static string RoleKeyword(ContextRole role) => role switch
    {
        ContextRole.Host => "host",
        ContextRole.SharedKernel => "shared-kernel",
        ContextRole.Plugin => "plugin",
        _ => "other",
    };

    private static ContextRole? ParseRole(string text) => text.Trim().ToLowerInvariant() switch
    {
        "host" => ContextRole.Host,
        "shared-kernel" or "sharedkernel" => ContextRole.SharedKernel,
        "plugin" => ContextRole.Plugin,
        "other" => ContextRole.Other,
        _ => null,
    };
}
