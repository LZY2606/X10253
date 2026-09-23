namespace PowerTiming.Core.Domain;

/// <summary>
/// Time source used by all history rows. Production is anchored at a fixed
/// reproducible epoch; tests advance a virtual clock deterministically.
/// </summary>
public abstract class EvidenceClock
{
    public abstract DateTimeOffset UtcNow { get; }

    public static EvidenceClock Fixed(DateTimeOffset value) => new FixedClock(value);

    public static EvidenceClock Virtual(DateTimeOffset start) => new VirtualTestClock(start);

    private sealed class FixedClock : EvidenceClock
    {
        public FixedClock(DateTimeOffset value) => UtcNow = value;
        public override DateTimeOffset UtcNow { get; }
    }

    private sealed class VirtualTestClock : EvidenceClock
    {
        private DateTimeOffset _current;

        public VirtualTestClock(DateTimeOffset start) => _current = start;

        public override DateTimeOffset UtcNow => _current;

        public void Advance(TimeSpan delta) => _current = _current.Add(delta);
    }
}
