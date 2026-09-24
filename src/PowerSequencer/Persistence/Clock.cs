namespace PowerSequencer.Persistence;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>Deterministic virtual time used by tests and case-pack generation.</summary>
public sealed class ManualClock : IClock
{
    public DateTimeOffset Now { get; private set; }
    public ManualClock(DateTimeOffset? start = null) => Now = start ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan span) => Now = Now.Add(span);
    public void Set(DateTimeOffset t) => Now = t;
}
