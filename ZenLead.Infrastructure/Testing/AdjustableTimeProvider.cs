namespace ZenLead.Infrastructure.Testing;

/// <summary>
/// The E2E environment's clock (T00): real time plus an offset that tests move through <c>/api/v1/test/clock</c>. Registered as the
/// app's <see cref="TimeProvider"/> in E2E only. Code that still calls <c>DateTime.UtcNow</c> directly doesn't follow it.
/// </summary>
public sealed class AdjustableTimeProvider(TimeProvider? inner = null) : TimeProvider
{
    private readonly TimeProvider _inner = inner ?? System;
    private readonly Lock _gate = new();
    private TimeSpan _offset;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _inner.GetUtcNow() + _offset;
    }

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public DateTimeOffset Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by), "The test clock only moves forward; use Set to go back.");
        lock (_gate) _offset += by;
        return GetUtcNow();
    }

    public DateTimeOffset Set(DateTimeOffset utcNow)
    {
        lock (_gate) _offset = utcNow - _inner.GetUtcNow();
        return GetUtcNow();
    }

    public DateTimeOffset Reset()
    {
        lock (_gate) _offset = TimeSpan.Zero;
        return GetUtcNow();
    }
}
