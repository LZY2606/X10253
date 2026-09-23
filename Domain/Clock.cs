namespace PSE.Domain;

/// <summary>虚拟时钟：测试使用固定起点的确定性时间，生产使用系统时间。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class VirtualClock : IClock
{
    private DateTimeOffset _now;
    public VirtualClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => _now;
    public DateTimeOffset Advance(TimeSpan by) { _now = _now.Add(by); return _now; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>固定 seed 的确定性随机源（seed=20260923）。</summary>
public sealed class DeterministicRng
{
    public const int FixedSeed = 20260923;
    private readonly Random _random;
    public DeterministicRng(int seed = FixedSeed) => _random = new Random(seed);
    public int Next(int maxExclusive) => _random.Next(maxExclusive);
    public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);
    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
