namespace Sfg.Toolkit.Tests;

/// <summary>
/// A synthetic five-project codebase with deliberately seeded boundary
/// violations. The seeds are the point: each test asserts the analyzer catches
/// a violation that exists by construction, with the exact file/line evidence.
/// </summary>
public sealed class TestCodebase : IDisposable
{
    public string Root { get; }

    public TestCodebase()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Write("Demo.SharedKernel/Demo.SharedKernel.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("Demo.SharedKernel/Contracts/CoreContracts.cs", """
            namespace Demo.SharedKernel.Contracts;

            public interface IOrdering
            {
                Task<Guid> PlaceOrder(string sku, int qty);
            }

            public interface ITime
            {
                DateTime Now();
            }

            public interface IAudit
            {
                Task LogAsync(string entry);
            }

            public interface IPluginModule
            {
                string Name { get; }
                string Version { get; }
            }
            """);
        Write("Demo.SharedKernel/Events/DomainEvent.cs", """
            namespace Demo.SharedKernel.Events;

            public abstract class DomainEvent
            {
            }

            public interface IEventHandler<in TEvent> where TEvent : DomainEvent
            {
                Task HandleAsync(TEvent evt);
            }
            """);

        Write("Demo.PluginA/Demo.PluginA.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\\Demo.SharedKernel\\Demo.SharedKernel.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Demo.PluginA/OrderModule.cs", """
            using Demo.SharedKernel.Contracts;

            namespace Demo.PluginA;

            public class OrderModule : IPluginModule
            {
                public string Name => "Orders";
                public string Version => "1.0.0";
            }
            """);
        Write("Demo.PluginA/OrderingService.cs", """
            using Demo.PluginA.Events;
            using Demo.SharedKernel.Contracts;
            using Demo.SharedKernel.Events;

            namespace Demo.PluginA.Services;

            public interface IRefund
            {
                Task RefundAsync(Guid id);
            }

            public class RefundService : IRefund
            {
                public Task RefundAsync(Guid id) => Task.CompletedTask;
            }

            public class Registry
            {
                public void Register()
                {
                    Bind(typeof(IRefund), typeof(RefundService));
                }

                private void Bind(Type service, Type implementation) { }
            }

            public class OrderingService : IOrdering, IEventHandler<OrderPlacedEvent>
            {
                private readonly ITime _clock;

                public OrderingService(ITime clock)
                {
                    _clock = clock;
                }

                public Task<Guid> PlaceOrder(string sku, int qty) => Task.FromResult(Guid.NewGuid());

                public Task HandleAsync(OrderPlacedEvent evt) => Task.CompletedTask;
            }
            """);
        Write("Demo.PluginA/Events/OrderPlacedEvent.cs", """
            using Demo.SharedKernel.Events;

            namespace Demo.PluginA.Events;

            public class OrderPlacedEvent : DomainEvent
            {
                public Guid OrderId { get; } = Guid.NewGuid();
            }
            """);
        Write("Demo.PluginA/OrderRules.cs", """
            namespace Demo.PluginA.Rules;

            public class OrderRules
            {
                public bool CanPlace(int qty) => qty > 0;
            }
            """);

        Write("Demo.PluginB/Demo.PluginB.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\\Demo.SharedKernel\\Demo.SharedKernel.csproj" />
                <ProjectReference Include="..\\Demo.PluginA\\Demo.PluginA.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Demo.PluginB/InventoryService.cs", """
            using Demo.PluginA.Services;

            namespace Demo.PluginB;

            public class InventoryService
            {
                private readonly IRefund _refunds;

                public InventoryService(IRefund refunds)
                {
                    _refunds = refunds;
                }
            }
            """);

        Write("Demo.Host/Demo.Host.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\\Demo.SharedKernel\\Demo.SharedKernel.csproj" />
                <ProjectReference Include="..\\Demo.PluginA\\Demo.PluginA.csproj" />
                <ProjectReference Include="..\\Demo.PluginB\\Demo.PluginB.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Demo.Host/Composition.cs", """
            using Demo.PluginA;
            using Demo.PluginA.Rules;
            using Demo.PluginA.Services;
            using Demo.SharedKernel.Contracts;

            namespace Demo.Host;

            public class Composition
            {
                public IOrdering Ordering { get; } = new OrderingService(new ClockStub());

                // seeded reach-around: the host consumes a public-but-non-contract type
                public OrderRules Rules { get; } = new OrderRules();

                public OrderModule Module { get; } = new OrderModule();

                private sealed class ClockStub { }
            }
            """);
    }

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public int LineOf(string relativePath, string needle)
    {
        var lines = File.ReadAllLines(Path.Combine(Root, relativePath));
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        throw new Xunit.Sdk.XunitException($"needle '{needle}' not found in {relativePath}");
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}