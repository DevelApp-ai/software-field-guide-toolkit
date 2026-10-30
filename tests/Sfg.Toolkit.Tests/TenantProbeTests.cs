using System.Text.Json;
using Sfg.Toolkit.TenantProbes;
using Xunit;

namespace Sfg.Toolkit.Tests;

public sealed class TenantProbeTests
{
    private static (EfModelScan Model, TenantAppFixture Fixture) Scan(TenantProbeConfig? config = null)
    {
        var fixture = new TenantAppFixture();
        try
        {
            return (EfModelScanner.Scan(fixture.Root, config ?? TenantProbeConfig.Default), fixture);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static (ProbeSuite Suite, TenantAppFixture Fixture) Generate(TenantProbeConfig? config = null)
    {
        var fixture = new TenantAppFixture();
        try
        {
            var model = EfModelScanner.Scan(fixture.Root, config ?? TenantProbeConfig.Default);
            var suite = TenantProbeGenerator.Generate(fixture.Root, Path.Combine(fixture.Root, "..", "probes-out"), model, config ?? TenantProbeConfig.Default);
            return (suite, fixture);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static string File(ProbeSuite suite, string name) => suite.Files.Single(f => f.Path == name).Content;

    [Fact]
    public void Scan_FindsDbContextWithExactFileLineEvidence()
    {
        var (model, fixture) = Scan();
        try
        {
            Assert.Equal("AppDbContext", model.Context.Name);
            Assert.Equal("Demo.App", model.Context.Namespace);
            Assert.EndsWith("Demo.App/Demo.App.csproj", model.Context.ProjectPath);
            Assert.Equal("Demo.App/AppDbContext.cs", model.Context.File);
            Assert.Equal(fixture.LineOf("Demo.App/AppDbContext.cs", "public class AppDbContext"), model.Context.Line);
            Assert.Equal("net8.0", model.TargetFramework);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Scan_InventoriesEntitiesWithNamespacesAndEvidence()
    {
        var (model, fixture) = Scan();
        try
        {
            Assert.Equal(2, model.Entities.Count);

            var widget = Assert.Single(model.Entities, e => e.Name == "Widget");
            Assert.Equal("Demo.App", widget.Namespace);
            Assert.Equal("Widgets", widget.DbSetProperty);
            Assert.Equal("Demo.App/AppDbContext.cs", widget.File);
            Assert.Equal(fixture.LineOf("Demo.App/AppDbContext.cs", "DbSet<Widget> Widgets"), widget.Line);

            var gadget = Assert.Single(model.Entities, e => e.Name == "Gadget");
            Assert.Equal("Demo.App.Domain", gadget.Namespace);
            Assert.Equal("Gadgets", gadget.DbSetProperty);
            Assert.Equal(fixture.LineOf("Demo.App/AppDbContext.cs", "DbSet<Gadget> Gadgets"), gadget.Line);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Scan_RejectsMultipleContextsWithoutConfig()
    {
        var fixture = new TenantAppFixture();
        try
        {
            fixture.SeedSecondContext();
            var error = Assert.Throws<InvalidOperationException>(() => EfModelScanner.Scan(fixture.Root, TenantProbeConfig.Default));
            Assert.Contains("ReportingDbContext", error.Message);
            Assert.Contains("AppDbContext", error.Message);
            Assert.Contains("dbContext", error.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Scan_ConfiguredDbContextNameSelectsAndMissingNameErrorsWithEvidence()
    {
        var fixture = new TenantAppFixture();
        try
        {
            fixture.SeedSecondContext();
            var model = EfModelScanner.Scan(fixture.Root, new TenantProbeConfig { DbContext = "ReportingDbContext" });
            Assert.Equal("ReportingDbContext", model.Context.Name);

            var error = Assert.Throws<InvalidOperationException>(() =>
                EfModelScanner.Scan(fixture.Root, new TenantProbeConfig { DbContext = "NopeContext" }));
            Assert.Contains("NopeContext", error.Message);
            Assert.Contains("AppDbContext (Demo.App/AppDbContext.cs", error.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Scan_ErrorsWhenEntityDeclarationIsMissingWithEvidence()
    {
        var fixture = new TenantAppFixture();
        try
        {
            fixture.SeedPhantomEntity();
            var error = Assert.Throws<InvalidOperationException>(() => EfModelScanner.Scan(fixture.Root, TenantProbeConfig.Default));
            Assert.Contains("'Phantom'", error.Message);
            Assert.Contains("AppDbContext.cs", error.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Generator_EmitsCompilableProjectWithCorrectReferences()
    {
        var (suite, fixture) = Generate();
        try
        {
            var csproj = File(suite, "TenantProbes.csproj");
            Assert.Contains("ProjectReference", csproj);
            Assert.Contains("Demo.App.csproj\" />", csproj);
            Assert.Contains("../", csproj);
            Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj);
            Assert.Contains("Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"8.0.11\"", csproj);
            Assert.Contains("Include=\"xunit\" Version=\"2.9.2\"", csproj);

            Assert.Equal(8, suite.Files.Count);
            var names = suite.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[]
                {
                    "CrossTenantReadProbes.cs",
                    "DisabledStateFailClosedProbes.cs",
                    "PoolCheckoutProbes.cs",
                    "ProbeHarness.cs",
                    "README.md",
                    "SaveGuardProbes.cs",
                    "TenantProbes.csproj",
                    "manifest.json",
                },
                names);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Generator_CoversEveryEntityInAllProbeClasses()
    {
        var (suite, fixture) = Generate();
        try
        {
            foreach (var probeFile in new[] { "CrossTenantReadProbes.cs", "PoolCheckoutProbes.cs" })
            {
                var content = File(suite, probeFile);
                Assert.Contains("Seed<Widget>()", content);
                Assert.Contains("Seed<Gadget>()", content);
                Assert.Contains("Set<Widget>()", content);
                Assert.Contains("Set<Gadget>()", content);
            }

            var saveGuard = File(suite, "SaveGuardProbes.cs");
            Assert.Contains("Seed<Widget>()", saveGuard);
            Assert.Contains("Seed<Gadget>()", saveGuard);
            Assert.Contains("AddTenantRow<Widget>(ProbeHarness.TenantB);", saveGuard);

            var disabled = File(suite, "DisabledStateFailClosedProbes.cs");
            Assert.Contains("Widget_read_fails_closed_when_tenant_context_absent", disabled);
            Assert.Contains("Gadget_write_fails_closed_when_tenant_context_absent", disabled);
            Assert.Contains("Assert.Empty(rows)", disabled);

            Assert.Contains("Widget_save_with_foreign_tenant_id_fails_closed", saveGuard);
            Assert.Contains("ThrowsAnyAsync", saveGuard);

            var cross = File(suite, "CrossTenantReadProbes.cs");
            Assert.Contains("TenantB", cross);
            Assert.Contains("using Demo.App.Domain;", cross);
            Assert.Contains("using Xunit;", cross);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Generator_HarnessFollowsConfiguredConventions()
    {
        var config = new TenantProbeConfig
        {
            TenantA = "acme",
            TenantB = "globex",
            TenantProperty = "OrgId",
            ContextTenantProperty = "CurrentOrgId",
        };
        var (suite, fixture) = Generate(config);
        try
        {
            var harness = File(suite, "ProbeHarness.cs");
            Assert.Contains("""public const string TenantIdProperty = "OrgId";""", harness);
            Assert.Contains("""public const string TenantA = "acme";""", harness);
            Assert.Contains("""public const string TenantB = "globex";""", harness);
            Assert.Contains("Context.CurrentOrgId = TenantA;", harness);

            var cross = File(suite, "CrossTenantReadProbes.cs");
            Assert.Contains("harness.Context.CurrentOrgId = ProbeHarness.TenantB;", cross);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Generator_ManifestListsEntitiesWithFileLineEvidence()
    {
        var (suite, fixture) = Generate();
        try
        {
            var manifest = File(suite, "manifest.json");
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            Assert.Equal("sfg tenant-probes 0.11.0", root.GetProperty("generator").GetString());
            Assert.Equal("AppDbContext", root.GetProperty("dbContext").GetString());
            Assert.Equal("net8.0", root.GetProperty("targetFramework").GetString());
            var entities = root.GetProperty("entities");
            Assert.Equal(2, entities.GetArrayLength());
            // entities are sorted by name: Gadget, then Widget; file/line cite the DbSet declaration.
            Assert.Equal("Gadget", entities[0].GetProperty("name").GetString());
            Assert.Equal("Demo.App.Domain", entities[0].GetProperty("namespace").GetString());
            Assert.Equal("Gadgets", entities[0].GetProperty("dbSet").GetString());
            Assert.Equal("Demo.App/AppDbContext.cs", entities[0].GetProperty("file").GetString());
            Assert.True(entities[0].GetProperty("line").GetInt32() > 0);
            Assert.Equal("Widget", entities[1].GetProperty("name").GetString());
            var probed = root.GetProperty("probedContext");
            Assert.Equal("Demo.App/AppDbContext.cs", probed.GetProperty("file").GetString());
            Assert.True(probed.GetProperty("line").GetInt32() > 0);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Generator_OutputIsByteIdenticalAcrossRuns()
    {
        var fixture = new TenantAppFixture();
        try
        {
            // Same scan, two generation runs: byte-identical output, including
            // the target-relative ProjectReference path.
            var model = EfModelScanner.Scan(fixture.Root, TenantProbeConfig.Default);
            var outDir = Path.Combine(fixture.Root, "probes-out");
            var first = TenantProbeGenerator.Generate(fixture.Root, outDir, model, TenantProbeConfig.Default);
            var second = TenantProbeGenerator.Generate(fixture.Root, outDir, model, TenantProbeConfig.Default);
            Assert.Equal(first.Files.Count, second.Files.Count);
            foreach (var file in first.Files)
            {
                var twin = second.Files.Single(f => f.Path == file.Path);
                Assert.True(file.Content.AsSpan().SequenceEqual<char>(twin.Content.AsSpan()), $"nondeterministic output: {file.Path}");
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }
}
