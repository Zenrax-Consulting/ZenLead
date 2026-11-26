namespace ZenLead.Domain.Enums;

/// <summary>Days a campaign may send on, in the campaign's time zone.</summary>
[Flags]
public enum SendDays
{
    None = 0,
    Mon = 1,
    Tue = 2,
    Wed = 4,
    Thu = 8,
    Fri = 16,
    Sat = 32,
    Sun = 64,
    Weekdays = Mon | Tue | Wed | Thu | Fri,
    All = Weekdays | Sat | Sun
}
