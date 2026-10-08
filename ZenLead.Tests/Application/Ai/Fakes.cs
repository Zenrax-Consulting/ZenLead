using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Tests.Application.Ai;

public class FakeEmailComposer : IEmailComposer
{
    public ComposedEmail Response { get; set; } = new("Subject", "Body", 42);
    public EmailComposeContext? LastContext { get; private set; }

    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        LastContext = context;
        return Task.FromResult(Response);
    }
}

public class FakeLeadRepositoryForAi : ILeadRepository
{
    private readonly Dictionary<Guid, Lead> _leads = [];

    public void Seed(Lead lead) => _leads[lead.Id] = lead;

    public Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default)
    {
        _leads[lead.Id] = lead;
        return Task.FromResult(lead);
    }

    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_leads.GetValueOrDefault(id));

    public Task<PagedResult<Lead>> SearchAsync(Guid workspaceId, LeadQuery query, CancellationToken ct = default)
    {
        var all = _leads.Values.Where(l => l.WorkspaceId == workspaceId).ToList();
        return Task.FromResult(new PagedResult<Lead>(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
    }

    public Task<IReadOnlyList<Guid>> ListIdsAsync(Guid workspaceId, LeadQuery query, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId).Select(l => l.Id).Take(max).ToList());

    public Task UpdateAsync(Lead lead, CancellationToken ct = default) => Task.CompletedTask;

    public Task SoftDeleteAsync(Lead lead, CancellationToken ct = default)
    {
        _leads.Remove(lead.Id);
        return Task.CompletedTask;
    }
}

public class FakeTokenUsageTracker : ITokenUsageTracker
{
    public List<AiUsageEntry> Entries { get; } = [];

    public Task RecordAsync(AiUsageEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<AiUsageSummary> GetSummaryAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var mine = Entries.Where(e => e.WorkspaceId == workspaceId).ToList();
        return Task.FromResult(new AiUsageSummary(
            mine.Count, mine.Sum(e => (long)e.PromptTokens + e.CompletionTokens), mine.Sum(e => e.EstimatedCostUsd)));
    }
}
