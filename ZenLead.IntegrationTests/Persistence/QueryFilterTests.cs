using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Persistence;

/// <summary>
/// The tenant query filter as SQL Server actually translates it (F11): for every <see cref="ITenantEntity"/> in the model, found by
/// reflection so a new entity is covered automatically, a row in workspace A is invisible to workspace B and to a context with no
/// workspace, and <c>ForWorkspace(A)</c> returns exactly A's rows while still honouring soft delete.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class QueryFilterTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private Guid _a, _b;

    public async Task InitializeAsync()
    {
        await sql.ResetAsync();
        await using var db = sql.CreateContext();
        _a = (await TenantRowSeed.SeedWorkspaceAsync(db, "A")).Id;
        _b = (await TenantRowSeed.SeedWorkspaceAsync(db, "B")).Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string> TenantEntities()
    {
        using var db = new ZenLead.Infrastructure.Persistence.ZenLeadDbContext(
            new DbContextOptionsBuilder<ZenLead.Infrastructure.Persistence.ZenLeadDbContext>().UseSqlServer("Server=.;Database=model-only").Options,
            new FakeCurrentWorkspace());
        return new TheoryData<string>(TenantRowSeed.TenantEntityTypes(db).Select(t => t.Name));
    }

    [Fact]
    public async Task TheSeed_HasARowOfEveryTenantEntity_SoNoCheckBelowIsVacuous()
    {
        await using var db = sql.CreateContext();
        foreach (var type in TenantRowSeed.TenantEntityTypes(db))
            Assert.True(await TenantRowSeed.CountStoredLiveAsync(db, type, _a) > 0, $"No {type.Name} row seeded; add one to TenantRowSeed.");
    }

    [Theory]
    [MemberData(nameof(TenantEntities))]
    public async Task ARowInWorkspaceA_IsInvisibleToWorkspaceB_AndToNoWorkspace(string entity)
    {
        await using var asB = sql.CreateContext(_b);
        await using var asNobody = sql.CreateContext(null);
        await using var asA = sql.CreateContext(_a);
        var type = TypeOf(asA, entity);

        var stored = await TenantRowSeed.CountStoredLiveAsync(asA, type, _a);
        Assert.Equal(stored, await TenantRowSeed.CountVisibleAsync(asA, type));
        Assert.Equal(await TenantRowSeed.CountStoredLiveAsync(asB, type, _b), await TenantRowSeed.CountVisibleAsync(asB, type));   // B sees only B
        Assert.Equal(0, await TenantRowSeed.CountVisibleAsync(asNobody, type));
    }

    [Theory]
    [MemberData(nameof(TenantEntities))]
    public async Task ForWorkspace_ReturnsExactlyThatWorkspacesRows_WhateverTheAmbientWorkspace(string entity)
    {
        foreach (Guid? ambient in new Guid?[] { null, _b, _a })
        {
            await using var db = sql.CreateContext(ambient);
            var type = TypeOf(db, entity);
            Assert.Equal(await TenantRowSeed.CountStoredLiveAsync(db, type, _a), await TenantRowSeed.CountForWorkspaceAsync(db, type, _a));
        }
    }

    [Fact]
    public async Task ForWorkspace_StillHidesSoftDeletedLeads()
    {
        await using var db = sql.CreateContext();

        var visible = await db.Leads.ForWorkspace(_a).ToListAsync();
        var all = await db.Leads.IgnoreQueryFilters().Where(l => l.WorkspaceId == _a).ToListAsync();

        Assert.Contains(all, l => l.DeletedAt != null);
        Assert.DoesNotContain(visible, l => l.DeletedAt != null);
        Assert.Equal(all.Count - 1, visible.Count);
    }

    [Fact]
    public async Task SavingATenantRowWithNoWorkspace_TakesTheCurrentWorkspace()
    {
        await using var db = sql.CreateContext(_b);
        var company = new Company { Id = Guid.NewGuid(), Name = "Stamped", CreatedAt = DateTime.UtcNow };   // WorkspaceId left empty
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        await using var check = sql.CreateContext();
        Assert.Equal(_b, (await check.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == company.Id)).WorkspaceId);
    }

    private static Type TypeOf(ZenLead.Infrastructure.Persistence.ZenLeadDbContext db, string name)
        => TenantRowSeed.TenantEntityTypes(db).Single(t => t.Name == name);
}
