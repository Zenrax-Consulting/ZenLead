using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Scheduling;

/// <summary>When a campaign may send: allowed weekdays and a daily [Start, End) range, both in <see cref="Zone"/>'s local time.</summary>
public record SendWindow(TimeZoneInfo Zone, TimeOnly Start, TimeOnly End, SendDays Days)
{
    public bool IsValid => Start < End && Days != SendDays.None;
    public bool Allows(DayOfWeek day) => (Days & ToFlag(day)) != 0;
    private static SendDays ToFlag(DayOfWeek d) => (SendDays)(1 << (((int)d + 6) % 7));   // Mon=1 … Sun=64
}
