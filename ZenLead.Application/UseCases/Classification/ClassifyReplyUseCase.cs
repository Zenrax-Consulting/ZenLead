using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Classification;
using ZenLead.Application.Sending;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Classification;

/// <summary>
/// The body of <c>ClassifyReplyJob</c> (F26): classify one inbound human reply and apply <see cref="ClassificationEffects"/>.
/// Never runs in the webhook path. Idempotent: an already-classified or auto-reply message is left alone. A bad model answer or an
/// exhausted budget leaves the message Unclassified (still in the inbox); transient provider errors propagate for Hangfire to retry.
/// </summary>
public class ClassifyReplyUseCase(
    IInboxClassificationStore store,
    IReplyClassifier classifier,
    IAiBudget budget,
    ITokenUsageTracker usage,
    AiPricing pricing,
    ClassificationEffects effects,
    IOptions<ClassificationOptions> options,
    ILogger<ClassifyReplyUseCase> logger)
{
    public const int MaxSubjectChars = 300;

    public async Task ExecuteAsync(Guid workspaceId, Guid messageId, CancellationToken ct)
    {
        var o = options.Value;
        var input = await store.LoadForClassificationAsync(workspaceId, messageId, ct);
        if (input is null || input.Message.Direction != MessageDirection.Inbound || input.Message.IsAutoReply
            || input.Message.ClassifiedBy != ClassificationSource.None) return;        // idempotent; auto-replies were labelled by headers

        if (await budget.CheckAsync(workspaceId, ct) == BudgetState.Exceeded)
        {
            logger.LogInformation("Reply {MessageId}: workspace AI budget exceeded; left Unclassified.", messageId);
            await store.MarkUnclassifiedAsync(workspaceId, messageId, ClassificationNotes.Budget, ct);   // visible in the inbox, no AI spend
            return;
        }

        var replyText = ReplyTextOf(input);
        ClassificationResult result;
        try
        {
            result = await classifier.ClassifyAsync(new ReplyClassificationInput(
                Truncate(replyText, o.MaxReplyChars)!, Truncate(input.OriginalSubject, MaxSubjectChars), Truncate(input.OriginalBody, o.MaxOriginalChars)), ct);
        }
        catch (AiProviderException ex) when (ex.Kind == AiProviderFailureKind.InvalidResponse)
        {
            await RecordUsageAsync(workspaceId, input, ex.PromptTokens, ex.CompletionTokens, succeeded: false, ct);   // billed even though unusable
            logger.LogWarning("Reply {MessageId}: model output invalid; left Unclassified.", messageId);
            await store.MarkUnclassifiedAsync(workspaceId, messageId, ClassificationNotes.InvalidOutput, ct);
            return;                                                                                  // handled: no Hangfire retry
        }
        // RateLimited / Unavailable / timeout propagate → Hangfire retries; after the last attempt the message simply stays Unclassified

        await RecordUsageAsync(workspaceId, input, result.PromptTokens, result.CompletionTokens, succeeded: true, ct);

        var confidence = result.Confidence;
        if (result.Classification == ReplyClassification.Unsubscribe && confidence < o.AutoActionMinConfidence
            && UnsubscribeKeywords.LooksLikeUnsubscribeRequest(input.Message.TextBody))
            confidence = o.AutoActionMinConfidence;                                                   // backstop raises, never invents
        logger.LogInformation("Reply {MessageId} classified {Classification} ({Confidence:0.00}).", messageId, result.Classification, confidence);
        await effects.ApplyAsync(workspaceId, messageId, result.Classification, confidence, manual: false, note: null, ct);
    }

    /// <summary>The cleaned reply; falls back to the full text, then the subject, when stripping left nothing.</summary>
    public static string ReplyTextOf(ClassificationInput input)
        => !string.IsNullOrWhiteSpace(input.Message.TextBody) ? input.Message.TextBody
         : !string.IsNullOrWhiteSpace(input.Message.FullTextBody) ? input.Message.FullTextBody
         : input.Message.Subject;

    private Task RecordUsageAsync(Guid workspaceId, ClassificationInput input, int prompt, int completion, bool succeeded, CancellationToken ct)
        => usage.RecordAsync(new AiUsageEntry(workspaceId, pricing.ClassifierModel, prompt, completion,
            pricing.EstimateCostUsd(pricing.ClassifierModel, prompt, completion), AiPurpose.ClassifyReply, input.CampaignId, input.EnrollmentId, succeeded), ct);

    public static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
