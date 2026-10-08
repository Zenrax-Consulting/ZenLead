using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Ai;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Ai;

public class EfTokenUsageTrackerTests
{
    private static DbContextOptions<ZenLeadDbContext> Options(string name)
        => new DbContextOptionsBuilder<ZenLeadDbContext>().UseInMemoryDatabase(name).Options;

    [Fact]
    public async Task Usage_SurvivesANewTrackerInstance_AndIsScopedPerWorkspace()
    {
        var dbName = Guid.NewGuid().ToString();
        var mine = Guid.NewGuid();

        using (var db = new ZenLeadDbContext(Options(dbName), new FakeCurrentWorkspace()))
        {
            var first = new EfTokenUsageTracker(db, NullLogger<EfTokenUsageTracker>.Instance);
            await first.RecordAsync(new AiUsageEntry(mine, "gpt-4o", 100, 50, 0.25m));
            await first.RecordAsync(new AiUsageEntry(mine, "gpt-4o", 10, 5, 0.05m));
            await first.RecordAsync(new AiUsageEntry(Guid.NewGuid(), "gpt-4o", 999, 999, 9m));
        }

        using var db2 = new ZenLeadDbContext(Options(dbName), new FakeCurrentWorkspace(mine)); // simulates a process restart
        var summary = await new EfTokenUsageTracker(db2, NullLogger<EfTokenUsageTracker>.Instance).GetSummaryAsync(mine);

        Assert.Equal(2, summary.Calls);
        Assert.Equal(165, summary.TotalTokens);
        Assert.Equal(0.30m, summary.EstimatedCostUsd);
    }

    [Fact]
    public void EstimateCost_UsesSeparateInputAndOutputPrices()
    {
        var pricing = new AiPricing { PricePer1KInputUsd = 0.0025m, PricePer1KOutputUsd = 0.01m };

        Assert.Equal(0.0125m, pricing.EstimateCostUsd(1000, 1000));
        Assert.Equal(0m, pricing.EstimateCostUsd(0, 0));
    }
}
