using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Discovery;

public class FakeDiscoveryRunRepository : IDiscoveryRunRepository
{
    public List<LeadDiscoveryRun> Runs { get; } = [];
    public List<string> Log { get; } = [];

    public Task AddAsync(LeadDiscoveryRun run, CancellationToken ct = default) { Runs.Add(run); Log.Add("add"); return Task.CompletedTask; }
    public Task<LeadDiscoveryRun?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));
    public Task<LeadDiscoveryRun?> GetForJobAsync(Guid id, Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult(Runs.FirstOrDefault(r => r.Id == id && r.WorkspaceId == workspaceId));
    public Task<IReadOnlyList<LeadDiscoveryRun>> ListAsync(Guid workspaceId, Guid? targetProfileId, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LeadDiscoveryRun>>(Runs.Where(r => r.WorkspaceId == workspaceId && (targetProfileId is null || r.TargetProfileId == targetProfileId)).Take(take).ToList());
    public Task<bool> HasActiveRunAsync(Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult(Runs.Any(r => r.WorkspaceId == workspaceId && r.Status is DiscoveryRunStatus.Queued or DiscoveryRunStatus.Running));
    public Task<int> GetCreditsUsedThisMonthAsync(Guid workspaceId, DateTime monthStartUtc, CancellationToken ct = default)
        => Task.FromResult(Runs.Where(r => r.WorkspaceId == workspaceId && r.CreatedAt >= monthStartUtc).Sum(r => r.CreditsUsed));
    public Task UpdateAsync(LeadDiscoveryRun run, CancellationToken ct = default) { Log.Add("update"); return Task.CompletedTask; }
}

public class FakeTargetProfileRepository : ITargetProfileRepository
{
    public List<TargetProfile> Profiles { get; } = [];
    public Task<TargetProfile?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Profiles.FirstOrDefault(p => p.Id == id));
    public Task<IReadOnlyList<TargetProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TargetProfile>>(Profiles.ToList());
    public Task<TargetProfile> AddAsync(TargetProfile profile, CancellationToken ct = default) { Profiles.Add(profile); return Task.FromResult(profile); }
    public Task UpdateAsync(TargetProfile profile, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(TargetProfile profile, CancellationToken ct = default) { Profiles.Remove(profile); return Task.CompletedTask; }
}

public class FakeJobScheduler(FakeDiscoveryRunRepository? runs = null) : IJobScheduler
{
    public List<(Guid RunId, Guid WorkspaceId, bool RunRowExisted)> Enqueued { get; } = [];
    public void EnqueueDiscoveryRun(Guid runId, Guid workspaceId)
        => Enqueued.Add((runId, workspaceId, runs?.Runs.Any(r => r.Id == runId) ?? true));
}

/// <summary>Returns pre-scripted pages by cursor index ("0", "1", ...), or generates fresh unique leads forever when none are scripted.</summary>
public class ScriptedLeadSource : ILeadSource
{
    private readonly IReadOnlyList<LeadSearchPage>? _pages;
    public ScriptedLeadSource(params LeadSearchPage[] pages) => _pages = pages.Length == 0 ? null : pages;

    public string Name => "Scripted";
    public Queue<Exception> FailuresToThrow { get; } = new();
    public List<(string? Cursor, int Limit)> Calls { get; } = [];
    public int GeneratedCreditsPerLead { get; set; } = 1;

    public Task<LeadSearchPage> SearchAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct)
    {
        Calls.Add((cursor, limit));
        if (FailuresToThrow.TryDequeue(out var failure)) throw failure;
        if (_pages is not null)
        {
            var i = cursor is null ? 0 : int.Parse(cursor);
            return Task.FromResult(_pages[i]);
        }
        var n = Calls.Count;
        var leads = Enumerable.Range(0, limit).Select(k => Lead($"gen{n}-{k}@gen.example.com")).ToList();
        return Task.FromResult(new LeadSearchPage(leads, n.ToString(), leads.Count * GeneratedCreditsPerLead));
    }

    public Task<int?> GetRemainingCreditsAsync(CancellationToken ct) => Task.FromResult<int?>(null);

    public static DiscoveredLead Lead(string? email, EmailVerificationStatus v = EmailVerificationStatus.Verified)
        => new("p-" + email, "Test", "Person", email, "CTO", "Acme", "acme.com", "Software", "US", "51-200", v);
}
