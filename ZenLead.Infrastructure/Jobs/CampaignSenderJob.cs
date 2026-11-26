using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Sending;
using ZenLead.Application.UseCases.Sending;

namespace ZenLead.Infrastructure.Jobs;

/// <summary>Recurring job "campaign-sender", every minute. Never overlaps itself; never retried (the next minute's run is the retry).</summary>
public class CampaignSenderJob(SendDueEmailsUseCase useCase, IOptions<SendingOptions> options, ILogger<CampaignSenderJob> logger)
{
    public const string RecurringJobId = "campaign-sender";

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        var s = await useCase.ExecuteAsync(ct);
        if (s.Total > 0)
            logger.LogInformation(
                "Sender run: {Sent} sent, {Failed} failed, {Blocked} blocked by allow-list, {Stopped} stopped, {Capped} capped, {Throttled} throttled, {Retrying} retrying, {Repaired} repaired, {Errors} errors",
                s.Sent, s.Failed, s.Blocked, s.Stopped, s.CapReached, s.Throttled, s.Retrying, s.Repaired, s.Errors);
    }
}
