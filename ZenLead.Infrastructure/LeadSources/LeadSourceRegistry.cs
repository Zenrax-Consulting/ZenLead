using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.LeadSources;

/// <summary>Maps <c>LeadSource:Provider</c> to an implementation. A new vendor is one sibling class plus one <see cref="Register"/> call.</summary>
public class LeadSourceRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, ILeadSource>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public LeadSourceRegistry Register(string name, Func<IServiceProvider, ILeadSource> factory)
    {
        _factories[name] = factory;
        return this;
    }

    public bool IsKnown(string name) => _factories.ContainsKey(name);

    public ILeadSource Resolve(string name, IServiceProvider services)
        => _factories.TryGetValue(name, out var factory)
            ? factory(services)
            : throw new InvalidOperationException($"Unknown LeadSource:Provider '{name}'. Known: {string.Join(", ", _factories.Keys)}.");

    /// <summary>Fake and Pdl; both classes must be registered in DI by the host.</summary>
    public static LeadSourceRegistry Default() => new LeadSourceRegistry()
        .Register("Fake", sp => sp.GetRequiredService<FakeLeadSource>())
        .Register(PdlLeadSource.ProviderName, sp => sp.GetRequiredService<PdlLeadSource>());
}
