namespace PowerSequencer.Domain;

/// <summary>
/// Unit normalization. Raw values are never altered at import; only the derived
/// layer converts to canonical volts / milliseconds. Unconvertible values stay
/// in place and produce a diagnostic instead of being dropped.
/// </summary>
public static class Units
{
    public static bool TryToVolts(double value, string unit, out double volts)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "v": volts = value; return true;
            case "mv": volts = value / 1000.0; return true;
            default: volts = value; return false;
        }
    }

    public static bool TryToMs(double value, string unit, out double ms)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "ms": ms = value; return true;
            case "s": ms = value * 1000.0; return true;
            case "us": ms = value / 1000.0; return true;
            default: ms = value; return false;
        }
    }
}
