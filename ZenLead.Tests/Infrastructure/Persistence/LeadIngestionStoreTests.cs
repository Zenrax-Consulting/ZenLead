using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

public class LeadIngestionStoreTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _ws = Guid.NewGuid();

    public LeadIngestionStoreTests()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = _ws, Name = "A", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Lead NewLead(string email) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = _ws, Name = "n", Email = email, Status = LeadStatus.New, CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task InsertLeads_WhenOneIsAlreadyTaken_TheRestStillLand()
    {
        using (var other = _db.CreateContext(null))
        {
            other.Leads.Add(NewLead("dup@acme.com"));
            await other.SaveChangesAsync();
        }

        var batch = new[] { NewLead("a@acme.com"), NewLead("dup@acme.com"), NewLead("b@acme.com") };
        using var ctx = _db.CreateContext(null);
        var landed = await new LeadIngestionStore(ctx).InsertLeadsAsync(batch, CancellationToken.None);

        Assert.Equal(2, landed.Count);
        Assert.Contains(batch[0].Id, landed);
        Assert.Contains(batch[2].Id, landed);
        Assert.DoesNotContain(batch[1].Id, landed);
    }

    [Fact]
    public async Task FindExisting_SeesSoftDeletedRows_WithoutAnAmbientWorkspace()
    {
        var lead = NewLead("gone@acme.com");
        lead.DeletedAt = DateTime.UtcNow;
        using (var seed = _db.CreateContext(null))
        {
            seed.Leads.Add(lead);
            await seed.SaveChangesAsync();
        }

        using var ctx = _db.CreateContext(null);
        var found = await new LeadIngestionStore(ctx).FindExistingAsync(_ws, ["gone@acme.com", "nope@acme.com"], CancellationToken.None);

        var existing = Assert.Single(found).Value;
        Assert.True(existing.IsDeleted);
        Assert.Equal(lead.Id, existing.Id);
    }
}
