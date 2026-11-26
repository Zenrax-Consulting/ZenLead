namespace ZenLead.Tests.Support;

/// <summary>A settable clock for use cases that take a <see cref="TimeProvider"/>.</summary>
public class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
