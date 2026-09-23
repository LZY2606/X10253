namespace PowerSeq.Core;

public interface IClock
{
    string Now();
}

public sealed class SystemClock : IClock
{
    public string Now() => DateTimeOffset.UtcNow.ToString("O");
}

/// <summary>Deterministic virtual time for tests and replays.</summary>
public sealed class FakeClock : IClock
{
    private long _ticks;
    public FakeClock(string start = "2026-01-01T00:00:00.0000000+00:00")
    {
        _ticks = DateTimeOffset.Parse(start).UtcTicks;
    }

    public string Now()
    {
        var t = new DateTimeOffset(_ticks, TimeSpan.Zero).ToString("O");
        _ticks += TimeSpan.TicksPerSecond;
        return t;
    }
}
