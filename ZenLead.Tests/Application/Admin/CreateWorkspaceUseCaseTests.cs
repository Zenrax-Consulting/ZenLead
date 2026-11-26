using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZenLead.Application;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Application.UseCases.Admin;
using ZenLead.Application.Validation.Admin;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Application.Auth;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Admin;

public class FakeAuditLog : IAuditLog
{
    public List<(string Action, Guid? Actor, string? TargetType, Guid? TargetId, object? Detail)> Entries { get; } = [];

    public Task WriteAsync(string action, Guid? actor, string? targetType, Guid? targetId, object? detail, CancellationToken ct = default)
    {
        Entries.Add((action, actor, targetType, targetId, detail));
        return Task.CompletedTask;
    }
}

public class CreateWorkspaceUseCaseTests
{
    private readonly FakeWorkspaceRepository _repo = new();
    private readonly FakeAuditLog _audit = new();
    private readonly FakeEmailSender _email = new();
    private readonly Guid _actor = Guid.NewGuid();
    private CreateWorkspaceUseCase Sut => new(_repo, _audit, _email,
        Options.Create(new AppOptions { PublicBaseUrl = "https://app.zenlead.test/" }), NullLogger<CreateWorkspaceUseCase>.Instance);

    [Fact]
    public async Task Creates_the_workspace_with_a_normalised_admin_email_and_audits_it()
    {
        var result = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("  Zenrax  ", " Admin@Zenrax.TEST "));

        Assert.False(result.IsDuplicate);
        var ws = Assert.Single(_repo.Created);
        Assert.Equal("Zenrax", ws.Name);
        Assert.Equal("admin@zenrax.test", ws.AdminEmail);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("workspace.created", entry.Action);
        Assert.Equal(_actor, entry.Actor);
        Assert.Equal(nameof(Workspace), entry.TargetType);
        Assert.Equal(ws.Id, entry.TargetId);
    }

    [Theory]
    [InlineData("Zenrax")]
    [InlineData("zenrax")]
    [InlineData("  ZENRAX ")]
    public async Task Duplicate_name_in_any_case_or_spacing_is_rejected(string name)
    {
        await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", "a@zenrax.test"));
        _audit.Entries.Clear();

        var result = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest(name, "b@zenrax.test"));

        Assert.True(result.IsDuplicate);
        Assert.Single(_repo.Created);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Losing_a_race_on_the_unique_index_is_reported_as_duplicate()
    {
        _repo.ThrowNameTakenOnCreate = true;
        var result = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", "a@zenrax.test"));
        Assert.True(result.IsDuplicate);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task New_workspace_appears_in_the_list_with_no_users()
    {
        var result = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", "a@zenrax.test"));

        var listed = Assert.Single(await _repo.ListWithUserCountsAsync());
        Assert.Equal(result.Workspace!.Id, listed.Id);
        Assert.Equal(0, listed.UserCount);
    }

    [Fact]
    public async Task Stores_the_requested_time_zone_or_defaults_to_UTC()
    {
        var dubai = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Dubai Co", "a@dubai.test", " Asia/Dubai "));
        var plain = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Plain Co", "a@plain.test"));

        Assert.Equal("Asia/Dubai", dubai.Workspace!.TimeZone);
        Assert.Equal("UTC", plain.Workspace!.TimeZone);
    }

    [Fact]
    public async Task Emails_the_admin_a_register_link()
    {
        await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", " Admin@Zenrax.TEST "));

        var mail = Assert.Single(_email.Sent);
        Assert.Equal("admin@zenrax.test", mail.To.Address);
        Assert.Contains("Zenrax", mail.Subject);
        Assert.Contains("https://app.zenlead.test/register", mail.TextBody);   // trailing slash on PublicBaseUrl is not doubled
        Assert.DoesNotContain("//register", mail.TextBody);
    }

    [Fact]
    public async Task A_failed_send_still_creates_the_workspace()
    {
        _email.Result = SendResult.Fail("SendGrid 503", retryable: true);

        var result = await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", "a@zenrax.test"));

        Assert.False(result.IsDuplicate);
        Assert.Single(_repo.Created);
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public async Task A_throwing_sender_still_creates_the_workspace()
    {
        var sut = new CreateWorkspaceUseCase(_repo, _audit, new ThrowingEmailSender(),
            Options.Create(new AppOptions { PublicBaseUrl = "https://app.zenlead.test" }), NullLogger<CreateWorkspaceUseCase>.Instance);

        var result = await sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", "a@zenrax.test"));

        Assert.False(result.IsDuplicate);
        Assert.Single(_repo.Created);
    }

    [Fact]
    public async Task No_email_is_sent_for_a_duplicate()
    {
        await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("Zenrax", "a@zenrax.test"));
        _email.Sent.Clear();

        await Sut.ExecuteAsync(_actor, new CreateWorkspaceRequest("zenrax", "b@zenrax.test"));

        Assert.Empty(_email.Sent);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
            => throw new HttpRequestException("network down");
    }
}

public class CreateWorkspaceRequestValidatorTests
{
    private readonly CreateWorkspaceRequestValidator _validator = new();

    [Theory]
    [InlineData("Zenrax", "admin@zenrax.test", null, true)]
    [InlineData("Zenrax", "admin@zenrax.test", "Asia/Dubai", true)]
    [InlineData("Zenrax", "admin@zenrax.test", "UTC", true)]
    [InlineData("Z", "admin@zenrax.test", null, false)]                 // too short
    [InlineData("   ", "admin@zenrax.test", null, false)]               // blank
    [InlineData(" Z ", "admin@zenrax.test", null, false)]               // too short after trimming
    [InlineData("Zenrax", "not-an-email", null, false)]
    [InlineData("Zenrax", "Admin <admin@zenrax.test>", null, false)]
    [InlineData("Zenrax", "admin@localhost", null, false)]              // no dot in the domain
    [InlineData("Zenrax", "", null, false)]
    [InlineData("Zenrax", "admin@zenrax.test", "Mars/Base", false)]
    public void Validates(string name, string email, string? timeZone, bool valid)
        => Assert.Equal(valid, _validator.Validate(new CreateWorkspaceRequest(name, email, timeZone)).IsValid);

    [Fact]
    public void Name_longer_than_200_is_rejected()
        => Assert.False(_validator.Validate(new CreateWorkspaceRequest(new string('a', 201), "a@b.test")).IsValid);
}

/// <summary>The real repository over SQLite: case-insensitive name check, race handling and the platform list.</summary>
public class WorkspaceRepositoryAdminTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task NameExists_is_case_and_whitespace_insensitive()
    {
        using var ctx = _db.CreateContext(null);
        var repo = new WorkspaceRepository(ctx);
        await repo.CreateAsync("Zenrax", "a@zenrax.test");

        Assert.True(await repo.NameExistsAsync(" zenRAX "));
        Assert.False(await repo.NameExistsAsync("Zenrax Two"));
    }

    [Fact]
    public async Task Unique_index_violation_becomes_WorkspaceNameTakenException()
    {
        using (var first = _db.CreateContext(null))
            await new WorkspaceRepository(first).CreateAsync("Zenrax", "a@zenrax.test");

        using var ctx = _db.CreateContext(null);
        await Assert.ThrowsAsync<WorkspaceNameTakenException>(() => new WorkspaceRepository(ctx).CreateAsync("Zenrax", "b@zenrax.test"));
        Assert.Empty(ctx.ChangeTracker.Entries<Workspace>().Where(e => e.State == EntityState.Added));   // the context stays usable
    }

    [Fact]
    public async Task List_counts_users_per_workspace()
    {
        using var ctx = _db.CreateContext(null);
        var repo = new WorkspaceRepository(ctx);
        var acme = await repo.CreateAsync("Acme", "admin@acme.test");
        await repo.CreateAsync("Zenrax", "admin@zenrax.test");
        ctx.Users.Add(new AppUser { Id = Guid.NewGuid(), UserName = "u1@acme.test", WorkspaceId = acme.Id });
        ctx.Users.Add(new AppUser { Id = Guid.NewGuid(), UserName = "u2@acme.test", WorkspaceId = acme.Id });
        await ctx.SaveChangesAsync();

        var list = await repo.ListWithUserCountsAsync();

        Assert.Equal(["Acme", "Zenrax"], list.Select(w => w.Name));
        Assert.Equal(2, list[0].UserCount);
        Assert.Equal("admin@acme.test", list[0].AdminEmail);
        Assert.Equal(0, list[1].UserCount);
    }
}
