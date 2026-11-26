using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.UseCases.Inbox;

namespace ZenLead.Api.Controllers.V1;

/// <summary>Unified inbox (F27): read replies, answer them, correct the classifier. Another workspace's thread is always 404.</summary>
[ApiController]
[Authorize]
[Route("api/v1/inbox")]
public class InboxController(
    IInboxRepository inbox,
    GetInboxThreadUseCase getThread,
    SendInboxReplyUseCase sendReply,
    ReclassifyThreadUseCase reclassify,
    IValidator<ThreadListQuery> queryValidator,
    IValidator<ReplyRequest> replyValidator,
    IValidator<ReclassifyRequest> reclassifyValidator) : ControllerBase
{
    [HttpGet("threads")]
    public async Task<ActionResult<PagedResult<ThreadListItem>>> List([FromQuery] ThreadListQuery query, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await queryValidator.ValidateAsync(query, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        return Ok(await inbox.SearchThreadsAsync(workspaceId, query, ct));
    }

    /// <summary>Opening a thread marks it read (idempotent; authenticated, so no link-prefetch risk).</summary>
    [HttpGet("threads/{id:guid}")]
    public async Task<ActionResult<ThreadDetail>> Get(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return await getThread.ExecuteAsync(workspaceId, id, ct) is { } detail ? Ok(detail) : NotFound();
    }

    [HttpPost("threads/{id:guid}/read")]
    public async Task<IActionResult> SetRead(Guid id, SetReadRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return await inbox.MarkReadAsync(workspaceId, id, request.IsRead, ct) ? NoContent() : NotFound();
    }

    [HttpPost("threads/{id:guid}/reply")]
    [EnableRateLimiting(RateLimiting.ReplyPolicy)]
    public async Task<ActionResult<MessageDto>> Reply(Guid id, ReplyRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId || this.UserId() is not { } userId) return Unauthorized();
        var validation = await replyValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var outcome = await sendReply.ExecuteAsync(workspaceId, userId, id, request, ct);
        return outcome.Kind switch
        {
            ReplyOutcomeKind.Sent => StatusCode(StatusCodes.Status201Created, outcome.Message),
            ReplyOutcomeKind.Duplicate => Ok(outcome.Message),
            ReplyOutcomeKind.Blocked => Conflict(new { code = "suppressed", message = outcome.Error }),
            ReplyOutcomeKind.Failed => StatusCode(StatusCodes.Status502BadGateway,
                new { code = "send_failed", message = outcome.Error, sentMessage = outcome.Message }),
            _ => NotFound()
        };
    }

    [HttpPost("threads/{id:guid}/classification")]
    public async Task<ActionResult<ThreadListItem>> Reclassify(Guid id, ReclassifyRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await reclassifyValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var outcome = await reclassify.ExecuteAsync(workspaceId, id, request.Classification, ct);
        return outcome.Kind switch
        {
            ReclassifyOutcomeKind.Ok => Ok(outcome.Thread),
            ReclassifyOutcomeKind.NothingToClassify => Conflict(new
            {
                code = "nothing_to_classify", message = "This conversation has no reply from the lead to classify."
            }),
            _ => NotFound()
        };
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountResponse>> UnreadCount(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return Ok(new UnreadCountResponse(await inbox.UnreadCountAsync(workspaceId, ct)));
    }
}
