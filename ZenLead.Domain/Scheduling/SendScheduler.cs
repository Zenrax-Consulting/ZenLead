namespace ZenLead.Domain.Scheduling;

public static class SendScheduler
{
    private const int MaxLookaheadDays = 14;

    /// <summary>
    /// Earliest UTC instant ≥ (after + delayDays calendar days, in the window's zone) that falls inside the send window.
    /// Calendar days (not 24-hour blocks) keep "3 days later at 10:00" at 10:00 across a DST change.
    /// </summary>
    public static DateTime NextSendAt(DateTime afterUtc, int delayDays, SendWindow window)
    {
        if (!window.IsValid) throw new ArgumentException("Send window is invalid.", nameof(window));
        ArgumentOutOfRangeException.ThrowIfNegative(delayDays);
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, window.Zone);
        var candidate = DateTime.SpecifyKind(local.AddDays(delayDays), DateTimeKind.Unspecified);    // calendar days → DST-safe

        for (var i = 0; i <= MaxLookaheadDays; i++)
        {
            var day = candidate.Date;
            var start = day + window.Start.ToTimeSpan();
            var end = day + window.End.ToTimeSpan();

            if (!window.Allows(day.DayOfWeek)) candidate = day.AddDays(1) + window.Start.ToTimeSpan();
            else if (candidate < start) candidate = start;
            else if (candidate >= end) candidate = day.AddDays(1) + window.Start.ToTimeSpan();   // End is exclusive
            else
            {
                var utc = ToUtc(candidate, window.Zone);
                // In a fall-back overlap the input itself may be the SECOND occurrence of an ambiguous wall-clock time; the
                // first occurrence would then lie in the past, so take the second one (smaller offset) to stay ≥ afterUtc.
                if (utc < afterUtc && window.Zone.IsAmbiguousTime(candidate))
                    utc = DateTime.SpecifyKind(candidate - window.Zone.GetAmbiguousTimeOffsets(candidate).Min(), DateTimeKind.Utc);
                return utc;
            }
        }
        throw new InvalidOperationException("No send slot found within the lookahead (window has no allowed days?).");
    }

    /// <summary>Local → UTC with DST edge cases resolved deterministically.</summary>
    internal static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))                       // spring-forward gap: that wall-clock time never happens → first valid moment after it
            local = local.AddHours(1);
        if (zone.IsAmbiguousTime(local))                     // fall-back overlap: take the FIRST occurrence (larger UTC offset)
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
