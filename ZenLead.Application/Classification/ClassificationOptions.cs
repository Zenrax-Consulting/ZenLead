namespace ZenLead.Application.Classification;

/// <summary>Bound from the "Classification" section (F26).</summary>
public class ClassificationOptions
{
    public const string SectionName = "Classification";

    /// <summary>Automatic actions (unsubscribe, OOO resume) only at or above this confidence.</summary>
    public decimal AutoActionMinConfidence { get; set; } = 0.8m;
    public int OooResumeDelayDays { get; set; } = 7;
    public int MaxReplyChars { get; set; } = 2000;
    public int MaxOriginalChars { get; set; } = 1500;
}
