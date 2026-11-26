namespace ZenLead.Application.Classification;

/// <summary>Values of <c>InboxMessage.ClassificationNote</c> (≤ 100 chars).</summary>
public static class ClassificationNotes
{
    public const string Budget = "budget";
    public const string AiFailed = "ai_failed";
    public const string LowConfidence = "low_confidence";
    public const string InvalidOutput = "invalid_output";
    public const string AutoHeader = "auto_header";
}
