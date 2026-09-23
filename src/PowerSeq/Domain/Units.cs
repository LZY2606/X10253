namespace PowerSeq.Domain;

/// <summary>A domain quantity that always carries its unit (coordinate basis).</summary>
public readonly record struct Quantity(double Value, string Unit)
{
    public override string ToString() => $"{Value} {Unit}";
}

public static class Units
{
    public static bool TryToMs(double value, string unit, out double ms)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "ms": ms = value; return true;
            case "us": case "µs": ms = value / 1000.0; return true;
            case "s": ms = value * 1000.0; return true;
            default: ms = 0; return false;
        }
    }

    public static bool TryToVolts(double value, string unit, out double volts)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "v": volts = value; return true;
            case "mv": volts = value / 1000.0; return true;
            default: volts = 0; return false;
        }
    }
}
