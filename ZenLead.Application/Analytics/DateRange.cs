using ZenLead.Application.Dtos.Analytics;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.Analytics;

/// <summary>An inclusive range of local days and its UTC bounds: [<see cref="FromUtc"/>, <see cref="ToUtcExclusive"/>).</summary>
public record ResolvedRange(DateOnly From, DateOnly To, DateTime FromUtc, DateTime ToUtcExclusive)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

public static class DateRange
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    /// <summary>
    /// Resolves a query in the workspace zone. Missing <c>To</c> = today (local); missing <c>From</c> = <c>To</c> − 29 days. Boundaries
    /// are local midnights converted DST-safely (<see cref="SendScheduler.ToUtc"/>), and <c>To</c> is inclusive (we add one local day).
    /// </summary>
    public static bool TryResolve(AnalyticsQuery query, TimeZoneInfo zone, DateTime nowUtc, out ResolvedRange range, out string? error)
    {
        range = default!;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone));
        var to = query.To ?? (query.From is { } f && f > today ? f : today);
        var from = query.From ?? to.AddDays(-(DefaultDays - 1));

        if (from > to)
        {
            error = "'from' must be on or before 'to'.";
            return false;
        }
        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
        {
            error = $"The range can be at most {MaxDays} days.";
            return false;
        }
        if (from.Year < 2000 || to.Year > 9000)
        {
            error = "The range is out of bounds.";
            return false;
        }

        range = new ResolvedRange(from, to, LocalMidnightUtc(from, zone), LocalMidnightUtc(to.AddDays(1), zone));
        error = null;
        return true;
    }

    public static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo zone) => SendScheduler.ToUtc(day.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>The local day a UTC instant falls on.</summary>
    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
}
