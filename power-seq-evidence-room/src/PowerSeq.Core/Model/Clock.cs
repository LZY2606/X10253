namespace PowerSeq.Core.Model;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// 固定 seed 的虚拟时钟：仅通过 Advance 推进，保证测试可重放。
/// </summary>
public sealed class VirtualClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; }

    public VirtualClock(DateTimeOffset? origin = null)
    {
        // 固定起点：2026-01-01T00:00:00Z
        UtcNow = origin ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
