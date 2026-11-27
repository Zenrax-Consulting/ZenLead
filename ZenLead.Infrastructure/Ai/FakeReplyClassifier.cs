using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Ai;

/// <summary>
/// Deterministic stand-in for the OpenAI reply classifier, selected by <c>Ai:Provider=Fake</c> (Development and E2E only).
/// Tests steer it with markers in the reply text:
/// <list type="bullet">
/// <item><c>[[unsubscribe]]</c> → Unsubscribe 0.99 (auto-action)</item>
/// <item><c>[[ooo]]</c> → OutOfOffice 0.95</item>
/// <item><c>[[not-interested]]</c> → NotInterested 0.95</item>
/// <item><c>[[review]]</c> → Unsubscribe 0.50 (consequential but uncertain, so it needs review)</item>
/// <item>anything else → Interested 0.95</item>
/// </list>
/// </summary>
public class FakeReplyClassifier : IReplyClassifier
{
    public const int PromptTokens = 80;
    public const int CompletionTokens = 5;

    public Task<ClassificationResult> ClassifyAsync(ReplyClassificationInput input, CancellationToken ct = default)
    {
        var text = input.ReplyText ?? "";
        var (label, confidence) =
            Has(text, "[[unsubscribe]]") ? (ReplyClassification.Unsubscribe, 0.99m)
            : Has(text, "[[ooo]]") ? (ReplyClassification.OutOfOffice, 0.95m)
            : Has(text, "[[not-interested]]") ? (ReplyClassification.NotInterested, 0.95m)
            : Has(text, "[[review]]") ? (ReplyClassification.Unsubscribe, 0.50m)
            : (ReplyClassification.Interested, 0.95m);
        return Task.FromResult(new ClassificationResult(label, confidence, PromptTokens, CompletionTokens));
    }

    private static bool Has(string text, string marker) => text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
