using ZenLead.Domain.Scheduling;

namespace ZenLead.Tests.Domain;

public class TimeZonesTests
{
    [Theory]
    [InlineData("Asia/Dubai")]
    [InlineData("Europe/London")]
    [InlineData("UTC")]
    public void Known_IANA_ids_are_found(string id)
    {
        Assert.True(TimeZones.TryFind(id, out var zone));
        Assert.NotNull(zone);
    }

    [Fact]
    public void Dubai_resolves_to_plus_four_hours()
    {
        Assert.True(TimeZones.TryFind("Asia/Dubai", out var zone));
        Assert.Equal(TimeSpan.FromHours(4), zone.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Base")]
    public void Unknown_or_blank_ids_are_rejected_and_fall_back_to_utc(string? id)
    {
        Assert.False(TimeZones.TryFind(id, out var zone));
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }
}
