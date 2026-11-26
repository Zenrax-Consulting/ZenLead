using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

/// <param name="ReplyText">The reply with quoted history stripped — untrusted third-party text.</param>
public record ReplyClassificationInput(string ReplyText, string? OriginalSubject, string? OriginalBody);

/// <param name="Classification">Always one of Interested, NotInterested, Unsubscribe, OutOfOffice (never Unclassified/Bounce).</param>
public record ClassificationResult(ReplyClassification Classification, decimal Confidence, int PromptTokens, int CompletionTokens);

/// <summary>Classifies an inbound reply (F26). Implemented in Infrastructure; call sites never see Semantic Kernel/OpenAI types.</summary>
public interface IReplyClassifier
{
    /// <exception cref="AiProviderException">RateLimited/Unavailable (transient), or InvalidResponse (carries the billed token counts).</exception>
    Task<ClassificationResult> ClassifyAsync(ReplyClassificationInput input, CancellationToken ct = default);
}
