using System.Reflection;
using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Infrastructure.Persistence;

namespace Angadi.Tests.Architecture;

/// <summary>
/// Enforces the dependency rule (ARCHITECTURE.md §1): Maui → Application → Domain ← Infrastructure.
/// A violation fails CI, so layering can't erode as the codebase grows.
/// </summary>
public class LayerRulesTests
{
    private static readonly Assembly Domain = typeof(Paise).Assembly;
    private static readonly Assembly App = typeof(Result).Assembly;
    private static readonly Assembly Infra = typeof(AngadiDb).Assembly;

    private static string[] Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name ?? "").ToArray();

    [Fact]
    public void Domain_HasZeroDependencies()
    {
        var allowed = new[] { "System.Runtime", "System.Collections", "System.Linq", "netstandard", "System.Private.CoreLib" };
        var bad = Refs(Domain).Where(r => !allowed.Contains(r) && !r.StartsWith("System.", StringComparison.Ordinal)).ToArray();
        Assert.True(bad.Length == 0, "Domain must reference only the BCL, found: " + string.Join(", ", bad));
    }

    [Fact]
    public void Application_DoesNotReferenceInfrastructureOrUi()
    {
        var refs = Refs(App);
        Assert.DoesNotContain("Angadi.Infrastructure", refs);
        Assert.DoesNotContain("Angadi.Maui", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase) || r.StartsWith("sqlite-net", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.Maui", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("SkiaSharp", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_DoesNotReferenceUi()
    {
        var refs = Refs(Infra);
        Assert.DoesNotContain("Angadi.Maui", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.Maui", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_EntitiesCarryNoPersistenceAttributes()
    {
        // ADR-12: storage shapes live in Infrastructure row classes only.
        var offenders = Domain.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(inherit: false).Select(a => (t, a)))
            .Where(x => x.a.GetType().Namespace?.StartsWith("SQLite", StringComparison.Ordinal) == true)
            .Select(x => x.t.Name)
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Money_IsNeverStoredAsFloatingPoint()
    {
        // ADR-8: no decimal/double/float properties on Domain entities or Infrastructure rows.
        var floaty = new[] { typeof(decimal), typeof(double), typeof(float), typeof(decimal?), typeof(double?), typeof(float?) };
        var types = Domain.GetTypes().Where(t => t.Namespace?.StartsWith("Angadi.Domain", StringComparison.Ordinal) == true && t.IsClass)
            .Concat(Infra.GetTypes().Where(t => t.Name.EndsWith("Row", StringComparison.Ordinal)));
        var offenders = types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (t, p)))
            .Where(x => floaty.Contains(x.p.PropertyType))
            .Select(x => $"{x.t.Name}.{x.p.Name}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Floating-point money/qty found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryCommandAndQuery_HasExactlyOneHandlerRegistered()
    {
        // A missing registration would only show up when a shopkeeper taps the button; catch it here.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Angadi.Application.DependencyInjection.AddAngadiApplication(services);

        var requestTypes = App.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                .Select(i => (Request: t, Result: i.GetGenericArguments()[0])))
            .ToList();

        Assert.NotEmpty(requestTypes);
        var missing = new List<string>();
        foreach (var (request, result) in requestTypes)
        {
            var handler = typeof(IRequestHandler<,>).MakeGenericType(request, result);
            var count = services.Count(d => d.ServiceType == handler);
            if (count != 1) missing.Add($"{request.Name} ({count} registrations)");
        }
        Assert.True(missing.Count == 0, "Handler registration problems: " + string.Join(", ", missing));
    }

    [Fact]
    public void UserFacingErrorCodes_AreAllUnique()
    {
        var values = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}
