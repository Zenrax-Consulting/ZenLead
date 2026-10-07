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

    public Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Lead>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId).ToList());
}
