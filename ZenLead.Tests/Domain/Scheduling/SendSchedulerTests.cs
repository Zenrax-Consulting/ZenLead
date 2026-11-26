using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Tests.Domain.Scheduling;

/// <summary>IANA ids throughout; they resolve on Windows through ICU (see TimeZonesTests).</summary>
public class SendSchedulerTests
{
    private static TimeZoneInfo Zone(string id) => TimeZones.TryFind(id, out var z) ? z : throw new InvalidOperationException($"{id} does not resolve");

    private static SendWindow W(string zone, string start = "09:00", string end = "17:00", SendDays days = SendDays.Weekdays)
        => new(Zone(zone), TimeOnly.Parse(start), TimeOnly.Parse(end), days);

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ---- window basics (Asia/Dubai: UTC+4, no DST). 2026-03-11 is a Wednesday. ----

    [Fact]
    public void InsideWindow_IsReturnedUnchanged()
        => Assert.Equal(Utc(2026, 3, 11, 6), SendScheduler.NextSendAt(Utc(2026, 3, 11, 6), 0, W("Asia/Dubai")));        // 10:00 local

    [Fact]
    public void BeforeStart_MovesToStartTheSameDay()
        => Assert.Equal(Utc(2026, 3, 11, 5), SendScheduler.NextSendAt(Utc(2026, 3, 11, 3), 0, W("Asia/Dubai")));        // 07:00 → 09:00 local

    [Fact]
    public void ExactlyEnd_IsExclusive_NextAllowedDayAtStart()
        => Assert.Equal(Utc(2026, 3, 12, 5), SendScheduler.NextSendAt(Utc(2026, 3, 11, 13), 0, W("Asia/Dubai")));       // Wed 17:00 → Thu 09:00

    [Fact]
    public void ExactlyStart_IsAllowed()
        => Assert.Equal(Utc(2026, 3, 11, 5), SendScheduler.NextSendAt(Utc(2026, 3, 11, 5), 0, W("Asia/Dubai")));

    [Fact]
    public void FridayAtEnd_WithWeekdays_MovesToMonday()
        => Assert.Equal(Utc(2026, 3, 16, 5), SendScheduler.NextSendAt(Utc(2026, 3, 13, 13), 0, W("Asia/Dubai")));       // Fri 17:00 → Mon 09:00

    [Fact]
    public void ThreeDaysFromThursday_LandsOnSunday_MovesToMonday()
        => Assert.Equal(Utc(2026, 3, 16, 5), SendScheduler.NextSendAt(Utc(2026, 3, 12, 6), 3, W("Asia/Dubai")));        // Thu 10:00 + 3d = Sun → Mon 09:00

    [Fact]
    public void TuesdayThursdayOnly()
    {
        var w = W("Asia/Dubai", days: SendDays.Tue | SendDays.Thu);
        Assert.Equal(Utc(2026, 3, 12, 5), SendScheduler.NextSendAt(Utc(2026, 3, 11, 6), 0, w));                          // Wed → Thu 09:00
        Assert.Equal(Utc(2026, 3, 17, 5), SendScheduler.NextSendAt(Utc(2026, 3, 12, 14), 0, w));                         // Thu 18:00 → Tue 09:00
    }

    [Fact]
    public void LocalDateAheadOfUtcDate_UsesTheLocalDay()
        => Assert.Equal(Utc(2026, 3, 11, 5), SendScheduler.NextSendAt(Utc(2026, 3, 10, 22), 0, W("Asia/Dubai")));       // Tue 22:00Z = Wed 02:00 local

    // ---- DST: America/New_York 2026 (spring forward 2026-03-08, fall back 2026-11-01) ----

    [Fact]
    public void SpringForwardGap_ResolvesToTheFirstValidMomentAfter()
    {
        var w = W("America/New_York", "02:30", "05:00", SendDays.All);
        // Sat 2026-03-07 23:00 EST → Sun 02:30 local, which never happens → 03:30 EDT
        Assert.Equal(Utc(2026, 3, 8, 7, 30), SendScheduler.NextSendAt(Utc(2026, 3, 8, 4), 0, w));
    }

    [Fact]
    public void DelayAcrossSpringForward_KeepsLocalTimeOfDay()
        // Fri 2026-03-06 10:00 EST (15:00Z) + 3 calendar days = Mon 10:00 EDT = 14:00Z (not 15:00Z, as 72 hours would give)
        => Assert.Equal(Utc(2026, 3, 9, 14), SendScheduler.NextSendAt(Utc(2026, 3, 6, 15), 3, W("America/New_York")));

    [Fact]
    public void FallBackOverlap_TakesTheFirstOccurrence()
    {
        var w = W("America/New_York", "01:30", "04:00", SendDays.All);
        // Sat 2026-10-31 20:00 EDT → Sun 01:30, which happens twice → the first (EDT) = 05:30Z
        Assert.Equal(Utc(2026, 11, 1, 5, 30), SendScheduler.NextSendAt(Utc(2026, 11, 1, 0), 0, w));
    }

    [Fact]
    public void FallBackOverlap_InputInTheSecondOccurrence_NeverGoesBackInTime()
    {
        var w = W("America/New_York", "01:00", "04:00", SendDays.All);
        var after = Utc(2026, 11, 1, 6, 10);                                                                             // 01:10 EST (second pass)
        Assert.Equal(after, SendScheduler.NextSendAt(after, 0, w));
    }

    [Fact]
    public void DelayAcrossFallBack_KeepsLocalTimeOfDay()
        // Fri 2026-10-30 10:00 EDT (14:00Z) + 3 days = Mon 10:00 EST = 15:00Z
        => Assert.Equal(Utc(2026, 11, 2, 15), SendScheduler.NextSendAt(Utc(2026, 10, 30, 14), 3, W("America/New_York")));

    // ---- other zones ----

    [Fact]
    public void London_AcrossBstStartAndEnd()
    {
        // Thu 2026-03-26 10:00 GMT + 4 = Mon 03-30 10:00 BST = 09:00Z; Thu 10-22 10:00 BST + 4 = Mon 10-26 10:00 GMT = 10:00Z
        Assert.Equal(Utc(2026, 3, 30, 9), SendScheduler.NextSendAt(Utc(2026, 3, 26, 10), 4, W("Europe/London")));
        Assert.Equal(Utc(2026, 10, 26, 10), SendScheduler.NextSendAt(Utc(2026, 10, 22, 9), 4, W("Europe/London")));
    }

    [Fact]
    public void Kolkata_HalfHourOffset()
    {
        Assert.Equal(Utc(2026, 3, 11, 8), SendScheduler.NextSendAt(Utc(2026, 3, 11, 8), 0, W("Asia/Kolkata")));          // 13:30 local
        Assert.Equal(Utc(2026, 3, 11, 3, 30), SendScheduler.NextSendAt(Utc(2026, 3, 11, 2), 0, W("Asia/Kolkata")));      // 07:30 → 09:00 local
    }

    [Fact]
    public void Auckland_SouthernHemisphereDstEnd()
        // Thu 2026-04-02 10:00 NZDT (Wed 21:00Z) + 4 = Mon 04-06 10:00 NZST (+12) = Sun 22:00Z
        => Assert.Equal(Utc(2026, 4, 5, 22), SendScheduler.NextSendAt(Utc(2026, 4, 1, 21), 4, W("Pacific/Auckland")));

    // ---- invalid / pathological ----

    [Theory]
    [InlineData("17:00", "09:00", SendDays.Weekdays)]
    [InlineData("09:00", "09:00", SendDays.Weekdays)]
    [InlineData("09:00", "17:00", SendDays.None)]
    public void InvalidWindow_Throws(string start, string end, SendDays days)
        => Assert.Throws<ArgumentException>(() => SendScheduler.NextSendAt(Utc(2026, 3, 11, 6), 0, W("UTC", start, end, days)));

    [Fact]
    public void OneAllowedWeekday_DelaySpanningTwoWeeks_StillFindsASlot()
        // Tue 2026-03-10 + 14 days = Tue 03-24 → next Monday 03-30 09:00 UTC
        => Assert.Equal(Utc(2026, 3, 30, 9), SendScheduler.NextSendAt(Utc(2026, 3, 10, 10), 14, W("UTC", days: SendDays.Mon)));

    [Fact]
    public void DaysOutsideTheWeek_ThrowsInsteadOfLoopingForever()
        => Assert.Throws<InvalidOperationException>(() => SendScheduler.NextSendAt(Utc(2026, 3, 10, 10), 0, W("UTC", days: (SendDays)128)));

    // ---- property check ----

    [Fact]
    public void RandomInputs_ResultIsNotEarlier_IsInsideTheWindow_AndIsIdempotent()
    {
        var zones = new[] { "UTC", "America/New_York", "Europe/London", "Asia/Dubai", "Asia/Kolkata", "Pacific/Auckland", "Australia/Sydney", "America/Los_Angeles" }
            .Select(Zone).ToArray();
        var rng = new Random(20261126);
        var from = Utc(2025, 1, 1, 0);

        for (var i = 0; i < 500; i++)
        {
            var zone = zones[rng.Next(zones.Length)];
            var startMinutes = rng.Next(0, 22 * 60);
            var start = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(startMinutes));
            var end = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Min(startMinutes + rng.Next(30, 10 * 60), 24 * 60 - 1)));
            var days = (SendDays)rng.Next(1, 128);
            var w = new SendWindow(zone, start, end, days);
            var after = from.AddMinutes(rng.Next(0, 4 * 365 * 24 * 60));
            var delay = rng.Next(0, 11);

            var result = SendScheduler.NextSendAt(after, delay, w);
            var label = $"#{i} {zone.Id} {start}-{end} {days} after={after:O} delay={delay} → {result:O}";

            Assert.True(result >= after, label);
            Assert.Equal(DateTimeKind.Utc, result.Kind);

            var local = TimeZoneInfo.ConvertTimeFromUtc(result, zone);
            var inWindow = w.Allows(local.DayOfWeek) && TimeOnly.FromDateTime(local) >= start && TimeOnly.FromDateTime(local) < end;
            if (!inWindow)
            {
                // documented gap-shift case: the wall-clock slot didn't exist, so the send moved one hour past it
                Assert.True(zone.IsInvalidTime(DateTime.SpecifyKind(local.AddHours(-1), DateTimeKind.Unspecified)), "outside window: " + label);
                continue;
            }
            Assert.True(SendScheduler.NextSendAt(result, 0, w) == result, "not idempotent: " + label);
        }
    }
}
