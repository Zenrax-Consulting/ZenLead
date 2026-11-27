using ZenLead.Application.Analytics;
using ZenLead.Application.Dtos.Analytics;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Tests.Application.Analytics;

public class RateMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    public void Zero_denominator_is_null_not_zero_or_nan(int n, int d) => Assert.Null(RateMath.Rate(n, d));

    [Fact]
    public void Ordinary_rates()
    {
        Assert.Equal(0d, RateMath.Rate(0, 10));
        Assert.Equal(0.25, RateMath.Rate(1, 4));
        Assert.Equal(1d, RateMath.Rate(7, 7));
    }

    [Fact]
    public void Numerator_above_denominator_is_clamped_to_100_percent() => Assert.Equal(1d, RateMath.Rate(12, 10));
}

public class DateRangeTests
{
    private static readonly TimeZoneInfo NewYork = Zone("America/New_York");
    private static readonly TimeZoneInfo Dubai = Zone("Asia/Dubai");
    private static readonly DateTime Now = new(2026, 3, 11, 6, 0, 0, DateTimeKind.Utc);

    private static TimeZoneInfo Zone(string id) => TimeZones.TryFind(id, out var z) ? z : throw new InvalidOperationException(id);

    private static ResolvedRange Resolve(DateOnly? from, DateOnly? to, TimeZoneInfo zone, DateTime? now = null)
    {
        Assert.True(DateRange.TryResolve(new AnalyticsQuery(from, to), zone, now ?? Now, out var r, out var error), error);
        return r;
    }

    [Fact]
    public void Default_is_the_last_30_local_days_ending_today()
    {
        var r = Resolve(null, null, Dubai);                                     // 06:00 UTC = 10:00 in Dubai on 11 March
        Assert.Equal(new DateOnly(2026, 3, 11), r.To);
        Assert.Equal(new DateOnly(2026, 2, 10), r.From);
        Assert.Equal(30, r.Days);
    }

    [Fact]
    public void Today_is_the_workspace_local_day_not_utc()
    {
        var lateUtc = new DateTime(2026, 3, 11, 22, 0, 0, DateTimeKind.Utc);    // already 12 March in Dubai, still 11 March in New York
        Assert.Equal(new DateOnly(2026, 3, 12), Resolve(null, null, Dubai, lateUtc).To);
        Assert.Equal(new DateOnly(2026, 3, 11), Resolve(null, null, NewYork, lateUtc).To);
    }

    [Fact]
    public void Edges_are_local_midnights_and_to_is_inclusive()
    {
        var r = Resolve(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1), Dubai);
        Assert.Equal(new DateTime(2026, 2, 28, 20, 0, 0, DateTimeKind.Utc), r.FromUtc);        // Dubai = UTC+4
        Assert.Equal(new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc), r.ToUtcExclusive);
    }

    [Fact]
    public void A_range_across_the_dst_change_has_contiguous_days_of_the_right_length()
    {
        var r = Resolve(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 11), NewYork);       // DST starts 2026-03-08 in New York
        Assert.Equal(7, r.Days);
        Assert.Equal(new DateTime(2026, 3, 5, 5, 0, 0, DateTimeKind.Utc), r.FromUtc);          // EST
        Assert.Equal(new DateTime(2026, 3, 12, 4, 0, 0, DateTimeKind.Utc), r.ToUtcExclusive);  // EDT
        Assert.Equal(TimeSpan.FromHours(23), DateRange.LocalMidnightUtc(new DateOnly(2026, 3, 9), NewYork) - DateRange.LocalMidnightUtc(new DateOnly(2026, 3, 8), NewYork));
    }

    [Fact]
    public void Only_from_or_only_to()
    {
        Assert.Equal(new DateOnly(2026, 3, 11), Resolve(new DateOnly(2026, 3, 1), null, Dubai).To);
        Assert.Equal(new DateOnly(2026, 1, 2), Resolve(null, new DateOnly(2026, 1, 31), Dubai).From);
    }

    [Fact]
    public void From_after_to_and_more_than_366_days_are_invalid()
    {
        Assert.False(DateRange.TryResolve(new AnalyticsQuery(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 1)), Dubai, Now, out _, out var e1));
        Assert.Contains("before", e1);
        Assert.False(DateRange.TryResolve(new AnalyticsQuery(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 2)), Dubai, Now, out _, out var e2));
        Assert.Contains("366", e2);
        Assert.True(DateRange.TryResolve(new AnalyticsQuery(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)), Dubai, Now, out _, out _));   // exactly 366
    }
}
