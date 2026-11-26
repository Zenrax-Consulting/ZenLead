using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

/// <summary>F27: the inbox queries on real SQLite with the tenant filter on (each repository gets a context for one workspace).</summary>
public sealed class InboxRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    public InboxRepositoryTests()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = _a, Name = "A", CreatedAt = T0 });
        ctx.Workspaces.Add(new Workspace { Id = _b, Name = "B", CreatedAt = T0 });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private InboxRepository Repo(Guid workspace, params IInterceptor[] interceptors) => new(_db.CreateContext(workspace, interceptors));

    private async Task<InboxSeed.Conversation> Seed(Guid workspace, int hoursAfterT0 = 0, string leadName = "Jane Doe", string subject = "Quick question",
        string? reply = "Sounds interesting", ReplyClassification classification = ReplyClassification.Unclassified, bool needsReview = false,
        Campaign? campaign = null, DateTime? deletedAt = null)
    {
        await using var ctx = _db.CreateContext(null);
        return await InboxSeed.ConversationAsync(ctx, workspace, T0.AddHours(hoursAfterT0), leadName, subject, reply,
            classification: classification, needsReview: needsReview, campaign: campaign, deletedAt: deletedAt);
    }

    private async Task AddMessageAsync(InboxMessage m)
    {
        await using var ctx = _db.CreateContext(null);
        ctx.InboxMessages.Add(m);
        await ctx.SaveChangesAsync();
    }

    private Task<Dtos> Search(Guid workspace, ThreadListQuery q) => Repo(workspace).SearchThreadsAsync(workspace, q, default)
        .ContinueWith(t => new Dtos(t.Result.Items, t.Result.Total));

    private sealed record Dtos(IReadOnlyList<ThreadListItem> Items, int Total);

    [Fact]
    public async Task Lists_threads_newest_first_with_paging_totals()
    {
        var oldest = await Seed(_a, 0, "Old");
        var middle = await Seed(_a, 5, "Mid");
        var newest = await Seed(_a, 10, "New");

        var page1 = await Search(_a, new ThreadListQuery(Page: 1, PageSize: 2));
        var page2 = await Search(_a, new ThreadListQuery(Page: 2, PageSize: 2));

        Assert.Equal(3, page1.Total);
        Assert.Equal([newest.Thread.Id, middle.Thread.Id], page1.Items.Select(i => i.Id));
        Assert.Equal([oldest.Thread.Id], page2.Items.Select(i => i.Id));
        var item = page1.Items[0];
        Assert.Equal("New", item.LeadName);
        Assert.Equal("Acme", item.CompanyName);
        Assert.Equal("Q4 outreach", item.CampaignName);
        Assert.False(item.IsRead);
    }

    [Fact]
    public async Task Threads_without_an_inbound_reply_are_not_in_the_inbox()
    {
        await Seed(_a, 0, "No reply", reply: null);           // F23 creates a thread per sent enrollment
        var replied = await Seed(_a, 1, "Replied");

        var result = await Search(_a, new ThreadListQuery());

        Assert.Equal(replied.Thread.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, await Repo(_a).UnreadCountAsync(_a, default));
    }

    [Fact]
    public async Task Snippet_is_the_latest_message_collapsed_to_one_line_and_count_includes_all_messages()
    {
        var c = await Seed(_a);
        await AddMessageAsync(InboxSeed.Outbound(_a, c.Thread.Id, T0.AddHours(2), "Re: Quick question", "Great,\n\nhow about   Tuesday?", c.Lead.Email, null));

        var item = Assert.Single((await Search(_a, new ThreadListQuery())).Items);

        Assert.Equal("Great, how about Tuesday?", item.Snippet);
        Assert.Equal(3, item.MessageCount);
    }

    [Fact]
    public async Task Long_snippets_are_cut()
    {
        await Seed(_a, reply: new string('x', 5000));
        var item = Assert.Single((await Search(_a, new ThreadListQuery())).Items);
        Assert.Equal(InboxRepository.SnippetLength, item.Snippet.Length);
    }

    [Fact]
    public async Task Filters_by_classification_unread_needs_review_and_campaign()
    {
        var interested = await Seed(_a, 0, "Interested", classification: ReplyClassification.Interested);
        var review = await Seed(_a, 1, "Review", classification: ReplyClassification.Unsubscribe, needsReview: true);
        var read = await Seed(_a, 2, "Read");
        await Repo(_a).MarkReadAsync(_a, read.Thread.Id, true, default);
        var sameCampaign = await Seed(_a, 3, "Same campaign", campaign: interested.Campaign);

        Assert.Equal([interested.Thread.Id],
            (await Search(_a, new ThreadListQuery(Classification: ReplyClassification.Interested))).Items.Select(i => i.Id));
        Assert.Equal([sameCampaign.Thread.Id, review.Thread.Id, interested.Thread.Id],
            (await Search(_a, new ThreadListQuery(Unread: true))).Items.Select(i => i.Id));
        Assert.Equal([read.Thread.Id], (await Search(_a, new ThreadListQuery(Unread: false))).Items.Select(i => i.Id));
        var flagged = Assert.Single((await Search(_a, new ThreadListQuery(NeedsReview: true))).Items);
        Assert.Equal(review.Thread.Id, flagged.Id);
        Assert.True(flagged.NeedsReview);
        Assert.Equal(3, (await Search(_a, new ThreadListQuery(NeedsReview: false))).Total);
        Assert.Equal([sameCampaign.Thread.Id, interested.Thread.Id],
            (await Search(_a, new ThreadListQuery(CampaignId: interested.Campaign.Id))).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Search_matches_lead_name_email_and_subject_with_like_wildcards_as_literals()
    {
        var byName = await Seed(_a, 0, "Ana Percent");
        var bySubject = await Seed(_a, 1, "Bob", subject: "Pricing 100% off_now");
        await Seed(_a, 2, "Carl", subject: "Pricing 1000 offXnow");

        Assert.Equal([byName.Thread.Id], (await Search(_a, new ThreadListQuery(Q: "percent"))).Items.Select(i => i.Id));
        Assert.Equal([bySubject.Thread.Id], (await Search(_a, new ThreadListQuery(Q: "100%"))).Items.Select(i => i.Id));
        Assert.Equal([bySubject.Thread.Id], (await Search(_a, new ThreadListQuery(Q: "off_now"))).Items.Select(i => i.Id));
        Assert.Equal([byName.Thread.Id], (await Search(_a, new ThreadListQuery(Q: byName.Lead.Email[5..17]))).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task The_list_is_one_page_query_plus_the_count_regardless_of_rows()
    {
        for (var i = 0; i < 6; i++) await Seed(_a, i, $"Lead {i}");

        var counter = new CommandCounter();
        var page = await Repo(_a, counter).SearchThreadsAsync(_a, new ThreadListQuery(PageSize: 25), default);

        Assert.Equal(6, page.Items.Count);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task Soft_deleted_leads_keep_their_conversation_readable()
    {
        var c = await Seed(_a, deletedAt: T0);

        Assert.Equal(c.Thread.Id, Assert.Single((await Search(_a, new ThreadListQuery())).Items).Id);
        Assert.NotNull(await Repo(_a).GetThreadAsync(_a, c.Thread.Id, default));
    }

    [Fact]
    public async Task Another_workspace_sees_nothing_of_a_thread()
    {
        var a = await Seed(_a);
        await Seed(_b, 1, "B lead");

        Assert.DoesNotContain(a.Thread.Id, (await Search(_b, new ThreadListQuery())).Items.Select(i => i.Id));
        var repo = Repo(_b);
        Assert.Null(await repo.GetThreadAsync(_b, a.Thread.Id, default));
        Assert.Null(await repo.GetListItemAsync(_b, a.Thread.Id, default));
        Assert.Null(await repo.GetThreadForReplyAsync(_b, a.Thread.Id, default));
        Assert.False(await repo.MarkReadAsync(_b, a.Thread.Id, true, default));
        // a forged workspace argument doesn't help either: the ambient filter (B) and the explicit check (A) never agree
        Assert.Null(await repo.GetThreadAsync(_a, a.Thread.Id, default));
        Assert.Equal(1, await Repo(_a).UnreadCountAsync(_a, default));
        Assert.Equal(1, await Repo(_b).UnreadCountAsync(_b, default));
    }

    [Fact]
    public async Task Mark_read_is_idempotent_and_mark_unread_works()
    {
        var c = await Seed(_a);

        Assert.True(await Repo(_a).MarkReadAsync(_a, c.Thread.Id, true, default));
        Assert.True(await Repo(_a).MarkReadAsync(_a, c.Thread.Id, true, default));
        Assert.Equal(0, await Repo(_a).UnreadCountAsync(_a, default));
        Assert.True(await Repo(_a).MarkReadAsync(_a, c.Thread.Id, false, default));
        Assert.Equal(1, await Repo(_a).UnreadCountAsync(_a, default));
    }

    [Fact]
    public async Task Thread_detail_has_messages_in_time_order()
    {
        var c = await Seed(_a);
        await AddMessageAsync(InboxSeed.Outbound(_a, c.Thread.Id, T0.AddHours(2), "Re: Quick question", "Answer", c.Lead.Email, null));

        var view = await Repo(_a).GetThreadAsync(_a, c.Thread.Id, default);

        Assert.NotNull(view);
        Assert.Equal([MessageDirection.Outbound, MessageDirection.Inbound, MessageDirection.Outbound], view.Messages.Select(m => m.Direction));
        Assert.Equal("Q4 outreach", view.CampaignName);
        Assert.Equal("Acme", view.Lead.Company?.Name);
    }

    [Fact]
    public async Task References_are_the_last_ten_message_ids_oldest_first_and_in_reply_to_is_the_latest_inbound()
    {
        var c = await Seed(_a, reply: null);
        for (var i = 1; i <= 12; i++)
            await AddMessageAsync(i % 2 == 0
                ? InboxSeed.Inbound(_a, c.Thread.Id, T0.AddMinutes(i), "Re", "r", c.Lead.Email, $"m{i}@x")
                : InboxSeed.Outbound(_a, c.Thread.Id, T0.AddMinutes(i), "Re", "o", c.Lead.Email, $"m{i}@x"));
        await AddMessageAsync(InboxSeed.Outbound(_a, c.Thread.Id, T0.AddMinutes(13), "Re", "manual reply, id unknown", c.Lead.Email, null));

        var repo = Repo(_a);
        var refs = await repo.GetReferencesAsync(_a, c.Thread.Id, default);

        Assert.Equal(Enumerable.Range(3, 10).Select(i => $"m{i}@x"), refs);
        Assert.Equal("m12@x", await repo.FindLatestInboundMessageIdAsync(_a, c.Thread.Id, default));
    }

    [Fact]
    public async Task The_same_client_request_id_can_be_inserted_once_per_thread()
    {
        var c = await Seed(_a);
        var requestId = Guid.NewGuid();
        InboxMessage Pending() => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = _a, ThreadId = c.Thread.Id, Direction = MessageDirection.Outbound, FromAddress = "us@x",
            ToAddress = c.Lead.Email, Subject = "Re", TextBody = "hi", OccurredAt = T0, ClientRequestId = requestId,
            DeliveryStatus = OutboundDeliveryStatus.Pending
        };

        Assert.True(await Repo(_a).TryAddOutboundPendingAsync(Pending(), default));
        Assert.False(await Repo(_a).TryAddOutboundPendingAsync(Pending(), default));
        await using var ctx = _db.CreateContext(null);
        Assert.Equal(1, await ctx.InboxMessages.IgnoreQueryFilters().CountAsync(m => m.ClientRequestId == requestId));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
