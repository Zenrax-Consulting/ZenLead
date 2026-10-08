namespace ZenLead.Application.Discovery;

/// <summary>Bound from config section <c>LeadSource</c>.</summary>
public class LeadSourceOptions
{
    public string Provider { get; set; } = "Fake";
    public int MonthlyCreditCap { get; set; } = 500;        // per workspace, calendar month UTC, in the provider's own credits
    public int MaxLeadsPerRun { get; set; } = 100;          // hard cap per run
    public int PageSize { get; set; } = 25;
    public int MaxPagesFactor { get; set; } = 5;            // stop after RequestedCount*factor/PageSize pages even if few imports (all-duplicates guard)
}
