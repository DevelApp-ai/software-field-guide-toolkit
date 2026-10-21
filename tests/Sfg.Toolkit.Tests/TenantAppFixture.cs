using Sfg.Toolkit.TenantProbes;

namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic EF Core application: one DbContext, two entities in two
/// namespaces (one expression-bodied DbSet, one auto-property), plus seeds
/// for failure modes (a second DbContext, a phantom DbSet entity).
/// </summary>
public sealed class TenantAppFixture : IDisposable
{
    public string Root { get; }

    public TenantAppFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-tenantprobes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Write("Demo.App/Demo.App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("Demo.App/AppDbContext.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Demo.App;

            public class AppDbContext : DbContext
            {
                public string? CurrentTenantId { get; set; }

                public AppDbContext(DbContextOptions options) : base(options)
                {
                }

                public DbSet<Widget> Widgets => Set<Widget>();
                public DbSet<Gadget> Gadgets { get; set; } = null!;
            }
            """);
        Write("Demo.App/Widget.cs", """
            namespace Demo.App;

            public class Widget
            {
                public int Id { get; set; }
                public string TenantId { get; set; } = string.Empty;
            }
            """);
        Write("Demo.App/Domain/Gadget.cs", """
            namespace Demo.App.Domain;

            public class Gadget
            {
                public int Id { get; set; }
                public string TenantId { get; set; } = string.Empty;
            }
            """);
    }

    public void SeedSecondContext()
    {
        Write("Demo.App/ReportingDbContext.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Demo.App.Reporting;

            public class ReportingDbContext : DbContext
            {
                public ReportingDbContext(DbContextOptions options) : base(options)
                {
                }

                public DbSet<Gadget> Gadgets => Set<Gadget>();
            }
            """);
    }

    public void SeedPhantomEntity()
    {
        // Overwrites the context with a DbSet<Phantom> whose entity type has
        // no class/record declaration anywhere under the root.
        Write("Demo.App/AppDbContext.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Demo.App;

            public class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions options) : base(options)
                {
                }

                public DbSet<Widget> Widgets => Set<Widget>();
                public DbSet<Phantom> Phantoms => Set<Phantom>();
            }
            """);
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

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup; temp directories are disposable.
        }
    }
}
