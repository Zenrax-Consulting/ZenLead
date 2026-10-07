# Feature 11 — Company, Lead Model, Ingestion Service & Tenant Foundation

**Branch:** `feature/company-lead-extension`
**Sprint:** 1 (first feature of Phase 2)
**Depends on:** Phase 1 merged. Everything else in Phase 2 depends on this.

## Goal
Four things in one migration-sized change, because each one is cheap now and expensive to retrofit:
1. `Company` entity and a richer `Lead` (company link, provenance, verification status, soft delete, uniqueness per workspace).
2. **`LeadIngestionService`** — the single code path every lead enters through (F14 discovery, F15 CSV, manual create).
3. **Tenant foundation** — `ICurrentWorkspace`, `ITenantEntity`, EF global query filter, `ForWorkspace()` helper for jobs.
4. Lead CRUD completion (`PUT`/`DELETE`) with status-transition rules, and `ComposeEmailUseCase` using company data.

## Files to add/modify

### Domain

**`ZenLead.Domain/Entities/ITenantEntity.cs`** (new)
```csharp
namespace ZenLead.Domain.Entities;

/// <summary>Marks a table that belongs to one workspace. Drives the EF query filter and insert stamping.</summary>
public interface ITenantEntity
{
    Guid WorkspaceId { get; set; }
}
```

**`ZenLead.Domain/Entities/Company.cs`** (new)
```csharp
namespace ZenLead.Domain.Entities;

public class Company : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Domain { get; set; }      // normalised: lowercase, no scheme, no "www."
    public string? Industry { get; set; }
    public string? Country { get; set; }
    public string? Size { get; set; }        // free text bucket from provider/CSV, e.g. "51-200" (used by F14 target profiles)
    public DateTime CreatedAt { get; set; }
}
```

**`ZenLead.Domain/Entities/Lead.cs`** (modified)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class Lead : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;   // always stored lowercase (see LeadEmail.Normalize)
    public string? Title { get; set; }
    public LeadStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public LeadSource Source { get; set; } = LeadSource.Manual;
    public Guid? SourceRunId { get; set; }               // discovery run id (F14) or CSV batch id (F15)
    public EmailVerificationStatus EmailVerificationStatus { get; set; } = EmailVerificationStatus.Unverified;

    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }             // soft delete: campaign history keeps its lead rows
}
```

**`ZenLead.Domain/Entities/AiUsageLog.cs`** (modified) — add `: ITenantEntity` to the class declaration (properties already match). Nothing else changes; F23 evolves this table into `AiGenerationLog`.

**`ZenLead.Domain/Enums/LeadStatus.cs`** (modified) — append `Bounced` **at the end** (the column is stored as int — `LeadConfiguration` has no `HasConversion`; inserting mid-list would renumber existing rows):
```csharp
public enum LeadStatus { New, Contacted, Replied, Unsubscribed, Bounced }
```

**`ZenLead.Domain/Enums/LeadSource.cs`** (new)
```csharp
namespace ZenLead.Domain.Enums;
public enum LeadSource { Manual, Csv, Discovery }
```

**`ZenLead.Domain/Enums/EmailVerificationStatus.cs`** (new)
```csharp
namespace ZenLead.Domain.Enums;
public enum EmailVerificationStatus { Unverified, Verified, Risky, Invalid }
```

**`ZenLead.Domain/Leads/LeadStatusRules.cs`** (new) — pure, unit-tested transition rules and the suppression predicate used by ingestion, enrollment and the sender.
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Leads;

public static class LeadStatusRules
{
    /// <summary>Statuses we must never email again. Ingestion, enrollment (F22) and the sender (F23) all use this.</summary>
    public static bool IsSuppressed(LeadStatus status) => status is LeadStatus.Unsubscribed or LeadStatus.Bounced;

    /// <summary>
    /// Manual/API transitions. System transitions (campaign send → Contacted, inbound reply → Replied,
    /// unsubscribe → Unsubscribed, bounce → Bounced) call <see cref="CanTransition"/> too, so they obey the same table.
    /// </summary>
    public static bool CanTransition(LeadStatus from, LeadStatus to)
    {
        if (from == to) return true;
        if (IsSuppressed(from)) return false;          // Unsubscribed/Bounced are terminal; a human re-consent flow is out of scope
        return to switch
        {
            LeadStatus.New => false,                    // never go back to New
            LeadStatus.Contacted => from == LeadStatus.New,
            LeadStatus.Replied => true,                 // a reply can arrive in any non-suppressed state
            LeadStatus.Unsubscribed or LeadStatus.Bounced => true,
            _ => false
        };
    }
}
```

**`ZenLead.Domain/Leads/LeadEmail.cs`** (new)
```csharp
using System.Net.Mail;

namespace ZenLead.Domain.Leads;

public static class LeadEmail
{
    public const int MaxLength = 256;

    /// <summary>Trim + lowercase. Returns null when the value is not a plausible single address.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().ToLowerInvariant();
        if (value.Length > MaxLength || value.Contains(' ') || value.Contains(',') || value.Contains(';')) return null;
        if (!MailAddress.TryCreate(value, out var parsed) || parsed.Address != value) return null; // rejects "Name <a@b.c>"
        var at = value.IndexOf('@');
        return at > 0 && value.IndexOf('.', at) > at + 1 ? value : null; // needs a dot in the domain part
    }
}
```

### Application

**`ZenLead.Application/Abstractions/ICurrentWorkspace.cs`** (new)
```csharp
namespace ZenLead.Application.Abstractions;

/// <summary>Workspace of the caller. Null outside an authenticated HTTP request (Hangfire jobs, webhooks, startup) — by design.</summary>
public interface ICurrentWorkspace
{
    Guid? WorkspaceId { get; }
}
```

**`ZenLead.Application/Leads/LeadIngestionModels.cs`** (new)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Leads;

public record CandidateLead(
    string? Name, string? Email, string? Title,
    string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize = null,
    EmailVerificationStatus Verification = EmailVerificationStatus.Unverified);

public record IngestionSource(LeadSource Source, Guid? SourceRunId);

public enum IngestionOutcome { Imported, Duplicate, Suppressed, Invalid }

/// <param name="Index">Position of the row in the input list, so callers can map back to CSV row numbers.</param>
public record IngestionRowResult(int Index, IngestionOutcome Outcome, string? Reason = null, Guid? LeadId = null);

public record IngestionSummary(IReadOnlyList<IngestionRowResult> Rows)
{
    public int Imported => Rows.Count(r => r.Outcome == IngestionOutcome.Imported);
    public int Duplicates => Rows.Count(r => r.Outcome == IngestionOutcome.Duplicate);
    public int Suppressed => Rows.Count(r => r.Outcome == IngestionOutcome.Suppressed);
    public int Invalid => Rows.Count(r => r.Outcome == IngestionOutcome.Invalid);
}
```

**`ZenLead.Application/Abstractions/ILeadIngestionStore.cs`** (new) — the persistence seam, so `LeadIngestionService` is pure logic.
```csharp
using ZenLead.Application.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

public record ExistingLead(Guid Id, LeadStatus Status, bool IsDeleted);

/// <summary>Identity of a company within a workspace: domain when we have one, otherwise case-insensitive name.</summary>
public record CompanyKey(string? Domain, string Name)
{
    public static CompanyKey? From(string? name, string? domain)
    {
        var d = NormalizeDomain(domain);
        var n = name?.Trim();
        if (d is null && string.IsNullOrEmpty(n)) return null;
        return new CompanyKey(d, string.IsNullOrEmpty(n) ? d! : n);
    }

    public static string? NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        v = v.Replace("https://", "").Replace("http://", "");
        if (v.StartsWith("www.")) v = v[4..];
        v = v.Split('/', '?', '#')[0];
        return v.Contains('.') ? v : null;
    }
}

public interface ILeadIngestionStore
{
    /// <summary>One batched query (<c>Email IN (...)</c>), including soft-deleted rows. Keys are lowercase emails.</summary>
    Task<IReadOnlyDictionary<string, ExistingLead>> FindExistingAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct);

    /// <summary>Find-or-create companies; returns the id for every requested key.</summary>
    Task<IReadOnlyDictionary<CompanyKey, Guid>> UpsertCompaniesAsync(
        Guid workspaceId, IReadOnlyCollection<(CompanyKey Key, string? Industry, string? Country, string? Size)> companies, CancellationToken ct);

    /// <summary>
    /// Inserts in one SaveChanges. If the unique (WorkspaceId, Email) index rejects the batch (a concurrent import won the race),
    /// falls back to row-by-row and returns only the ids that actually landed.
    /// </summary>
    Task<IReadOnlySet<Guid>> InsertLeadsAsync(IReadOnlyList<Lead> leads, CancellationToken ct);
}
```

**`ZenLead.Application/Leads/LeadIngestionService.cs`** (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;

namespace ZenLead.Application.Leads;

/// <summary>
/// The only way leads enter the system besides single manual create (which also uses it).
/// Callers pass already-parsed rows in batches (CSV: ~500, discovery: one provider page); this class never reads files or calls providers.
/// </summary>
public class LeadIngestionService(ILeadIngestionStore store)
{
    public async Task<IngestionSummary> IngestAsync(
        Guid workspaceId, IReadOnlyList<CandidateLead> rows, IngestionSource source, CancellationToken ct = default)
    {
        var results = new IngestionRowResult?[rows.Count];
        var firstSeen = new Dictionary<string, int>();           // email → index of first valid occurrence in this batch
        var candidates = new List<(int Index, string Email, CandidateLead Row)>();

        for (var i = 0; i < rows.Count; i++)
        {
            var email = LeadEmail.Normalize(rows[i].Email);
            if (email is null) { results[i] = new(i, IngestionOutcome.Invalid, "Invalid email address"); continue; }
            if (rows[i].Verification == EmailVerificationStatus.Invalid) { results[i] = new(i, IngestionOutcome.Invalid, "Provider marked email invalid"); continue; }
            if (!firstSeen.TryAdd(email, i)) { results[i] = new(i, IngestionOutcome.Duplicate, "Duplicate within this batch"); continue; }
            candidates.Add((i, email, rows[i]));
        }

        var existing = candidates.Count == 0
            ? new Dictionary<string, ExistingLead>()
            : (await store.FindExistingAsync(workspaceId, candidates.Select(c => c.Email).ToList(), ct)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var toInsert = new List<(int Index, string Email, CandidateLead Row)>();
        foreach (var c in candidates)
        {
            if (!existing.TryGetValue(c.Email, out var found)) { toInsert.Add(c); continue; }
            results[c.Index] = LeadStatusRules.IsSuppressed(found.Status)
                ? new(c.Index, IngestionOutcome.Suppressed, $"Lead is {found.Status}", found.Id)
                : new(c.Index, IngestionOutcome.Duplicate, found.IsDeleted ? "Previously deleted lead" : "Already exists", found.Id);
        }

        if (toInsert.Count > 0)
        {
            var companyRequests = toInsert
                .Select(t => (Key: CompanyKey.From(t.Row.CompanyName, t.Row.CompanyDomain), t.Row))
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!)
                .Select(g => (g.Key, Industry: g.Select(x => x.Row.Industry).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                              Country: g.Select(x => x.Row.Country).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                              Size: g.Select(x => x.Row.CompanySize).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))))
                .ToList();
            var companyIds = companyRequests.Count == 0
                ? new Dictionary<CompanyKey, Guid>()
                : (await store.UpsertCompaniesAsync(workspaceId, companyRequests, ct)).ToDictionary(kv => kv.Key, kv => kv.Value);

            var now = DateTime.UtcNow;
            var leads = toInsert.Select(t =>
            {
                var key = CompanyKey.From(t.Row.CompanyName, t.Row.CompanyDomain);
                return new Lead
                {
                    Id = Guid.NewGuid(), WorkspaceId = workspaceId,
                    Name = Truncate(string.IsNullOrWhiteSpace(t.Row.Name) ? t.Email.Split('@')[0] : t.Row.Name.Trim(), 200)!,
                    Email = t.Email, Title = Truncate(t.Row.Title?.Trim(), 200),
                    Status = LeadStatus.New, CreatedAt = now,
                    CompanyId = key is not null && companyIds.TryGetValue(key, out var cid) ? cid : null,
                    Source = source.Source, SourceRunId = source.SourceRunId,
                    EmailVerificationStatus = t.Row.Verification
                };
            }).ToList();

            var landed = await store.InsertLeadsAsync(leads, ct);
            for (var k = 0; k < toInsert.Count; k++)
                results[toInsert[k].Index] = landed.Contains(leads[k].Id)
                    ? new(toInsert[k].Index, IngestionOutcome.Imported, null, leads[k].Id)
                    : new(toInsert[k].Index, IngestionOutcome.Duplicate, "Created concurrently by another import");
        }

        return new IngestionSummary(results!);
    }

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
```
Each caller commits per batch it hands in; `IngestAsync` itself does not open a transaction (the store's `SaveChanges` calls are the transaction boundary), so an interrupted job leaves a clean prefix and re-running it is safe — rows already imported come back as `Duplicate`.

**`ZenLead.Application/Dtos/Leads/LeadDtos.cs`** (modified)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record CreateLeadRequest(string Name, string Email, string? Title, string? CompanyName = null, string? CompanyDomain = null);
public record UpdateLeadRequest(string Name, string? Title, LeadStatus Status, string? CompanyName, string? CompanyDomain);

public record CompanySummary(Guid Id, string Name, string? Domain, string? Industry, string? Country);

public record LeadResponse(
    Guid Id, string Name, string Email, string? Title, LeadStatus Status, DateTime CreatedAt,
    CompanySummary? Company, LeadSource Source, Guid? SourceRunId, EmailVerificationStatus EmailVerificationStatus);
```
The existing `LeadResponse(Id, Name, Email, Title, Status, CreatedAt)` positional signature changes — update `LeadsController.ToResponse` and `ZenLead.Tests/Api/LeadsControllerTests.cs`.

**`ZenLead.Application/Validation/Leads/UpdateLeadRequestValidator.cs`** (new)
```csharp
using FluentValidation;
using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Application.Validation.Leads;

public class UpdateLeadRequestValidator : AbstractValidator<UpdateLeadRequest>
{
    public UpdateLeadRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.CompanyName).MaximumLength(200);
        RuleFor(x => x.CompanyDomain).MaximumLength(253);
    }
}
```
Add `RuleFor(x => x.CompanyName).MaximumLength(200)` / `CompanyDomain` to the existing `CreateLeadRequestValidator`. Email is deliberately **not** editable (it is the lead's identity and the dedupe key).

**`ZenLead.Application/Abstractions/ILeadRepository.cs`** (modified) — keep the existing three methods (`GetByIdAsync` now also loads `Company`), add:
```csharp
Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default);              // includes Company, excludes soft-deleted (query filter)
Task UpdateAsync(Lead lead, CancellationToken ct = default);
Task SoftDeleteAsync(Lead lead, CancellationToken ct = default);
```
`ListByWorkspaceAsync` stays until F13 replaces it with a paged query (keep it so Sprint 1 PRs stay independent).

**`ZenLead.Application/UseCases/Ai/ComposeEmailUseCase.cs`** (modified) — pass company data; `Context` stays as an override/addition.
```csharp
composed = await composer.ComposeAsync(
    new EmailComposeContext(lead.Name, lead.Email, lead.Title, request.Context,
        lead.Company?.Name, lead.Company?.Industry, lead.Company?.Country), ct);
```
**`ZenLead.Application/Abstractions/IEmailComposer.cs`** — extend the record with trailing optional members so existing fakes/tests compile:
```csharp
public record EmailComposeContext(string LeadName, string LeadEmail, string? LeadTitle, string? AdditionalContext,
    string? CompanyName = null, string? CompanyIndustry = null, string? CompanyCountry = null);
```
**`ZenLead.Infrastructure/Ai/EmailComposer.cs`** — in `BuildUserMessage` add three lines (`Company: …`, `Industry: …`, `Country: …`, each `?? "unknown"`). The system prompt already forbids fabricating company facts; add one sentence: *"Only use the company details given below; never add facts about the company that are not listed."*

### Infrastructure

**`ZenLead.Infrastructure/Persistence/ZenLeadDbContext.cs`** (modified)
```csharp
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class ZenLeadDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public const string TenantFilter = "Tenant";
    public const string SoftDeleteFilter = "SoftDelete";

    private readonly ICurrentWorkspace _currentWorkspace;

    public ZenLeadDbContext(DbContextOptions<ZenLeadDbContext> options, ICurrentWorkspace currentWorkspace) : base(options)
        => _currentWorkspace = currentWorkspace;

    // Read through a property of the context so EF treats it as a per-context parameter, not a constant baked into the model.
    private Guid? CurrentWorkspaceId => _currentWorkspace.WorkspaceId;

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ZenLeadDbContext).Assembly);

        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
            SetTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);

        builder.Entity<Lead>().HasQueryFilter(SoftDeleteFilter, l => l.DeletedAt == null);
    }

    private static readonly MethodInfo SetTenantFilterMethod =
        typeof(ZenLeadDbContext).GetMethod(nameof(SetTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void SetTenantFilter<T>(ModelBuilder builder) where T : class, ITenantEntity
        => builder.Entity<T>().HasQueryFilter(TenantFilter, e => e.WorkspaceId == CurrentWorkspaceId);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampTenant();
        return base.SaveChangesAsync(ct);
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    /// <summary>New tenant rows with no workspace get the caller's. Rows that already carry one (jobs, ingestion) are left alone.</summary>
    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>().Where(e => e.State == EntityState.Added && e.Entity.WorkspaceId == Guid.Empty))
            entry.Entity.WorkspaceId = CurrentWorkspaceId
                ?? throw new InvalidOperationException($"{entry.Entity.GetType().Name} has no WorkspaceId and there is no current workspace.");
    }
}
```
> **Verify:** named query filters (`HasQueryFilter(name, expr)` and `IgnoreQueryFilters([names])`) are an EF Core 10 feature — confirm on the installed 10.0.12. Fallback if unavailable: one combined filter per entity (`e.WorkspaceId == CurrentWorkspaceId && e.DeletedAt == null` for `Lead`) and a second `ForWorkspace` variant that re-applies the soft-delete predicate manually.
>
> `AppUser`, `Workspace` and `RefreshToken` are **not** `ITenantEntity` — login/refresh happen before a workspace is known.

**`ZenLead.Infrastructure/Persistence/TenantQueryExtensions.cs`** (new)
```csharp
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public static class TenantQueryExtensions
{
    /// <summary>
    /// For code with no HTTP context (Hangfire jobs, webhooks): drop the ambient tenant filter and scope explicitly.
    /// Soft-delete and any other filters stay on. Never call this with a workspaceId taken from request input.
    /// </summary>
    public static IQueryable<T> ForWorkspace<T>(this IQueryable<T> query, Guid workspaceId) where T : class, ITenantEntity
        => query.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]).Where(e => e.WorkspaceId == workspaceId);
}
```

**`ZenLead.Infrastructure/Persistence/Configurations/EntityConfigurations.cs`** (modified)
```csharp
public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Domain).HasMaxLength(253);
        builder.Property(c => c.Industry).HasMaxLength(200);
        builder.Property(c => c.Country).HasMaxLength(100);
        builder.Property(c => c.Size).HasMaxLength(50);
        builder.HasIndex(c => new { c.WorkspaceId, c.Domain }).IsUnique().HasFilter("[Domain] IS NOT NULL");
        builder.HasIndex(c => new { c.WorkspaceId, c.Name });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.Property(l => l.Name).HasMaxLength(200);
        builder.Property(l => l.Email).HasMaxLength(256);
        builder.Property(l => l.Title).HasMaxLength(200);
        builder.HasIndex(l => l.WorkspaceId);
        builder.HasIndex(l => new { l.WorkspaceId, l.Email }).IsUnique();     // includes soft-deleted rows on purpose
        builder.HasIndex(l => new { l.WorkspaceId, l.SourceRunId });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(l => l.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Company).WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}
```
> **Decision recorded here:** the unique index covers soft-deleted rows. A deleted lead keeps reserving its email, so deleting an `Unsubscribed` lead and re-importing the address can never email them again. Ingestion reports such rows as `Duplicate` ("Previously deleted lead"). A "restore" action is a later add-on.

**`ZenLead.Infrastructure/Persistence/LeadRepository.cs`** (modified)
```csharp
public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default)
    => db.Leads.Include(l => l.Company).FirstOrDefaultAsync(l => l.Id == id, ct);

public async Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    => await db.Leads.Include(l => l.Company).Where(l => l.WorkspaceId == workspaceId).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);

public async Task UpdateAsync(Lead lead, CancellationToken ct = default)
{
    lead.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);                     // lead is tracked (loaded via GetByIdAsync in the same scope)
}

public async Task SoftDeleteAsync(Lead lead, CancellationToken ct = default)
{
    lead.DeletedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);
}
```

**`ZenLead.Infrastructure/Persistence/LeadIngestionStore.cs`** (new)
```csharp
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class LeadIngestionStore(ZenLeadDbContext db) : ILeadIngestionStore
{
    public async Task<IReadOnlyDictionary<string, ExistingLead>> FindExistingAsync(
        Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        var rows = await db.Leads
            .IgnoreQueryFilters()                                        // include soft-deleted AND work with no HTTP context
            .Where(l => l.WorkspaceId == workspaceId && emails.Contains(l.Email))
            .Select(l => new { l.Email, l.Id, l.Status, Deleted = l.DeletedAt != null })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Email, r => new ExistingLead(r.Id, r.Status, r.Deleted));
    }

    public async Task<IReadOnlyDictionary<CompanyKey, Guid>> UpsertCompaniesAsync(
        Guid workspaceId, IReadOnlyCollection<(CompanyKey Key, string? Industry, string? Country, string? Size)> companies, CancellationToken ct)
    {
        var domains = companies.Select(c => c.Key.Domain).Where(d => d != null).Cast<string>().ToList();
        var names = companies.Where(c => c.Key.Domain is null).Select(c => c.Key.Name.ToLower()).ToList();

        var existing = await db.Companies.ForWorkspace(workspaceId)
            .Where(c => (c.Domain != null && domains.Contains(c.Domain)) || (c.Domain == null && names.Contains(c.Name.ToLower())))
            .ToListAsync(ct);

        var result = new Dictionary<CompanyKey, Guid>();
        foreach (var (key, industry, country, size) in companies)
        {
            var match = key.Domain is not null
                ? existing.FirstOrDefault(c => c.Domain == key.Domain)
                : existing.FirstOrDefault(c => c.Domain == null && string.Equals(c.Name, key.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = new Company { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = key.Name, Domain = key.Domain,
                                      Industry = industry, Country = country, Size = size, CreatedAt = DateTime.UtcNow };
                db.Companies.Add(match);
                existing.Add(match);
            }
            else // fill blanks only; never overwrite what a human typed
            {
                match.Industry ??= industry; match.Country ??= country; match.Size ??= size;
            }
            result[key] = match.Id;
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlySet<Guid>> InsertLeadsAsync(IReadOnlyList<Lead> leads, CancellationToken ct)
    {
        db.Leads.AddRange(leads);
        try
        {
            await db.SaveChangesAsync(ct);
            return leads.Select(l => l.Id).ToHashSet();
        }
        catch (DbUpdateException)
        {
            // most likely the unique (WorkspaceId, Email) index: a concurrent import inserted one of ours. Retry one by one.
            db.ChangeTracker.Clear();
            var landed = new HashSet<Guid>();
            foreach (var lead in leads)
            {
                db.Leads.Add(lead);
                try { await db.SaveChangesAsync(ct); landed.Add(lead.Id); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); }
            }
            return landed;
        }
    }
}
```
> `Contains` on a list of ≤ ~500 emails translates to `IN (...)`; keep callers' batch size ≤ 1000 (SQL Server's parameter limit is 2100).

**Migration `AddCompanyAndLeadProvenance`** (generated, then hand-edited)
`dotnet ef migrations add AddCompanyAndLeadProvenance -p ZenLead.Infrastructure -s ZenLead.Api`. In `Up()`, **before** the `CreateIndex` for `(WorkspaceId, Email)` insert:
```csharp
// normalise first, then fail loudly if lowercasing produced collisions — never silently merge or drop leads
migrationBuilder.Sql("UPDATE Leads SET Email = LOWER(LTRIM(RTRIM(Email)))");
migrationBuilder.Sql("""
    IF EXISTS (SELECT 1 FROM Leads GROUP BY WorkspaceId, Email HAVING COUNT(*) > 1)
        THROW 50001, 'Duplicate (WorkspaceId, Email) leads exist. Merge or delete them before applying AddCompanyAndLeadProvenance.', 1;
    """);
```
New columns get defaults: `Source = 0 (Manual)`, `EmailVerificationStatus = 0`. `Down()` drops them. Add `HasData` nowhere.

**DI-related:** `ZenLead.Infrastructure/DependencyInjection.cs` is *not* introduced here (registrations stay in `Program.cs` like Phase 1). Add to `Program.cs`:

### Api

**`ZenLead.Api/HttpCurrentWorkspace.cs`** (new)
```csharp
using System.Security.Claims;
using ZenLead.Application.Abstractions;

namespace ZenLead.Api;

public class HttpCurrentWorkspace(IHttpContextAccessor accessor) : ICurrentWorkspace
{
    public Guid? WorkspaceId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("workspace_id"), out var id) && id != Guid.Empty ? id : null;
}
```

**`ZenLead.Api/Program.cs`** (modified)
```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentWorkspace, HttpCurrentWorkspace>();   // must precede AddDbContext users; scoped like the context
builder.Services.AddScoped<ILeadIngestionStore, LeadIngestionStore>();
builder.Services.AddScoped<LeadIngestionService>();
```

**`ZenLead.Api/Controllers/V1/LeadsController.cs`** (modified) — add (and use `LeadIngestionService` for `Create` so manual create dedupes identically):
```csharp
public class LeadsController(
    ILeadRepository leads, LeadIngestionService ingestion,
    IValidator<CreateLeadRequest> createValidator, IValidator<UpdateLeadRequest> updateValidator) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<LeadResponse>> Create(CreateLeadRequest request, CancellationToken ct)
    {
        // …existing workspace + validation guard…
        var summary = await ingestion.IngestAsync(workspaceId,
            [new CandidateLead(request.Name, request.Email, request.Title, request.CompanyName, request.CompanyDomain, null, null)],
            new IngestionSource(LeadSource.Manual, null), ct);
        var row = summary.Rows[0];
        return row.Outcome switch
        {
            IngestionOutcome.Imported => CreatedAtAction(nameof(GetById), new { id = row.LeadId }, ToResponse((await leads.GetByIdAsync(row.LeadId!.Value, ct))!)),
            IngestionOutcome.Invalid => ValidationProblem(new ModelStateDictionary().With(nameof(request.Email), row.Reason!)),
            _ => Conflict(new { message = row.Reason })     // Duplicate / Suppressed
        };
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LeadResponse>> Update(Guid id, UpdateLeadRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await updateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(ToModelState(validation));

        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != workspaceId) return NotFound();
        if (!LeadStatusRules.CanTransition(lead.Status, request.Status))
            return Conflict(new { message = $"Cannot change status from {lead.Status} to {request.Status}." });

        lead.Name = request.Name.Trim();
        lead.Title = request.Title?.Trim();
        lead.Status = request.Status;
        // company change goes through the same upsert the importers use
        // (store.UpsertCompaniesAsync via a small ILeadCompanyService, or inject ILeadIngestionStore directly)
        await leads.UpdateAsync(lead, ct);
        return Ok(ToResponse(lead));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != workspaceId) return NotFound();
        await leads.SoftDeleteAsync(lead, ct);
        return NoContent();
    }
}
```
Move `ToModelState` from `AuthController` into a shared `ZenLead.Api/ValidationExtensions.cs` (`ToModelState(this ValidationResult)`) now that three controllers want it; update `AuthController`/`AiController` to use it.

> Sprint-1 sequencing note: F13 later replaces `List` with the paged query; leave `List` working here.

### Tests (`ZenLead.Tests`)

**`Support/TestDb.cs`** (new) — shared by every DB test from here on.
```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support;

public class FakeCurrentWorkspace(Guid? id = null) : ICurrentWorkspace
{
    public Guid? WorkspaceId { get; set; } = id;
}

/// <summary>One shared in-memory SQLite database, many contexts — each with its own "current workspace", like separate requests.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var ctx = CreateContext(null);
        ctx.Database.EnsureCreated();
    }

    public ZenLeadDbContext CreateContext(Guid? workspaceId)
        => new(new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlite(_connection).Options, new FakeCurrentWorkspace(workspaceId));

    public void Dispose() => _connection.Dispose();
}
```
> `EnsureCreated` builds the real model, so filters and unique indexes are exercised. SQL Server-only index filters (`HasFilter("[Domain] IS NOT NULL")`) are valid SQLite syntax as written.

**Modified existing tests** (constructor change): `Infrastructure/Ai/EfTokenUsageTrackerTests.cs` (two `new ZenLeadDbContext(Options(name))` → add `, new FakeCurrentWorkspace()`; it stays on the EF InMemory provider, which is fine there), `Infrastructure/Identity/RefreshTokenServiceTests.cs` (the file-based SQLite helper — same extra argument). `FakeLeadRepository` (`Api/LeadsControllerTests.cs`) and `FakeLeadRepositoryForAi` (`Application/Ai/Fakes.cs`) implement the three new `ILeadRepository` methods; `new LeadsController(...)` gets the new constructor arguments (a `LeadIngestionService` over a `FakeLeadIngestionStore`).

**`Application/Leads/FakeLeadIngestionStore.cs`** (new) — dictionary-backed implementation of `ILeadIngestionStore` (unique by lowercase email per workspace; companies keyed by `CompanyKey`).

**`Application/Leads/LeadIngestionServiceTests.cs`** (new, PBI 11.6)
- invalid emails (`""`, `"not-an-email"`, `"Name <a@b.com>"`, no dot in domain, > 256 chars) → `Invalid` with reason, nothing inserted.
- `A@X.com` and `a@x.com` in one batch → first `Imported`, second `Duplicate`.
- email already in the workspace → `Duplicate` with the existing `LeadId`; same email in a **different** workspace → `Imported`.
- existing `Unsubscribed` / `Bounced` → `Suppressed`; existing soft-deleted `Contacted` → `Duplicate("Previously deleted lead")`.
- provider-`Invalid` verification → `Invalid`.
- company upsert: two rows same domain (`https://www.Acme.com/`) → one company; domain-less rows match by case-insensitive name; second import reuses the company and fills only blank fields.
- name blank → falls back to the email local-part; over-long name/title truncated to 200.
- store reports a race loser (not in `landed`) → `Duplicate("Created concurrently…")`.
- per-row `Index` maps back correctly with a mixed batch (invalid, dup, new, suppressed).

**`Domain/LeadStatusRulesTests.cs`** (new) — table-driven `CanTransition` / `IsSuppressed`.

**`Infrastructure/Persistence/LeadIngestionStoreTests.cs`** (new, SQLite via `TestDb`) — `InsertLeadsAsync` race fallback (pre-insert one duplicate through a second context, assert the rest land); `FindExistingAsync` sees soft-deleted rows.

**`Infrastructure/Persistence/TenantIsolationTests.cs`** (new, PBI 11.9 — the headline tests)
- Seed workspaces A and B with leads/companies. A context scoped to A lists only A's rows (`Leads`, `Companies`, `AiUsageLogs`); B's `GetByIdAsync(aLeadId)` returns `null`.
- A context with `WorkspaceId = null` returns **zero** rows from every tenant table (the "forgot to scope" safety net).
- `ForWorkspace(A)` on a null-workspace context returns A's rows and not B's, and still hides soft-deleted leads.
- unique `(WorkspaceId, Email)`: same email in A and B saves; same email twice in A throws `DbUpdateException`.
- `SaveChanges` stamps `WorkspaceId` on an added lead from the current workspace; adding with no workspace and no explicit id throws.
- Controller level (extend `LeadsControllerTests`): user in workspace B gets `NotFound` for `PUT`/`DELETE` on A's lead; the Phase 1 `GetById` cross-tenant test still passes unchanged.
- A reflection test: every entity type in the model that has a `WorkspaceId` property (other than `AppUser`) implements `ITenantEntity` — fails the build when someone adds a tenant table and forgets the filter.

**`Application/Ai/ComposeEmailUseCaseTests.cs`** — add a case: lead with a `Company` → `FakeEmailComposer.LastContext` carries company name/industry/country.

## Not in this feature
Paged/filtered list and bulk select (F13), target profiles and discovery (F14 — uses `Lead.Source`/`SourceRunId` added here), CSV (F15), a restore-deleted-lead action, any UI beyond the response-shape change (the Angular `Lead` model gets the new optional fields in F13).

## Verification
- `dotnet ef migrations add AddCompanyAndLeadProvenance …`, read `Up()`, `dotnet ef database update` on a LocalDB that has a few Phase 1 leads (including a mixed-case duplicate pair to see the migration fail loudly, then fix and re-run).
- `dotnet test ZenLead.Tests` — all Phase 1 tests plus the new ones green.
- Manual (`ZenLead.Api.http`): register two users in two workspaces; user A creates a lead; user B `GET /leads/{id}` → 404; B `DELETE` → 404; A `POST` same email again → 409; A `PUT` status `Contacted` → 200, then `PUT` back to `New` → 409.
- Generate a draft from the Angular lead detail page for a lead with a company and confirm the prompt includes it (log or fake).
