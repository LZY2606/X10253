namespace PowerEvidence.Domain;

/// <summary>带原始表示、单位与测量误差的量。原始字符串一经提交不可改写。</summary>
public sealed record Quantity(string Raw, string Unit, double Value, double Error)
{
    /// <summary>三值比较：考虑误差后是否确定高于阈值。null = 不确定。</summary>
    public bool? DefinitelyAbove(double threshold)
    {
        if (double.IsNaN(Value)) return null;
        if (Value - Error > threshold) return true;
        if (Value + Error < threshold) return false;
        return null;
    }
}

public sealed record Sample(double T, Quantity Voltage);
public sealed record RailWaveform(string Rail, IReadOnlyList<Sample> Samples);
public sealed record SignalSample(double T, string State);
public sealed record SignalWaveform(string Name, IReadOnlyList<SignalSample> Samples);
public sealed record ClockSpec(double Period, string Unit, double Error);

public sealed record WaveformBundle(
    ClockSpec Clock,
    IReadOnlyList<RailWaveform> Rails,
    IReadOnlyList<SignalWaveform> Signals);

public sealed record RailThreshold(
    string Rail, Quantity Rise, Quantity Fall, double Hysteresis, double MinStable, string TimeUnit);

public sealed record ThresholdSet(IReadOnlyList<RailThreshold> Rails)
{
    public RailThreshold? For(string rail) => Rails.FirstOrDefault(r => r.Rail == rail);
}

public sealed record DependencyEdge(string Before, string After, string Scope);
public sealed record DependencyGraph(IReadOnlyList<DependencyEdge> Edges)
{
    public IReadOnlyList<string> Nodes => Edges.SelectMany(e => new[] { e.Before, e.After }).Distinct().ToArray();
}

/// <summary>单位规范化只在派生层发生；未知单位返回 null 并保留原值。</summary>
public static class Units
{
    public static double? ToVolts(Quantity q) => q.Unit switch
    {
        "V" => q.Value,
        "mV" => q.Value * 1e-3,
        _ => null
    };

    public static double? ToMilliseconds(double value, string unit) => unit switch
    {
        "ms" => value,
        "us" => value * 1e-3,
        "s" => value * 1e3,
        _ => null
    };
}
