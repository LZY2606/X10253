namespace PowerSequencing.Core;

/// <summary>
/// A measured / specified value whose original textual representation and unit
/// are preserved verbatim.  Normalisation to the internal SI base happens only
/// in derived layers; values that cannot be converted stay in place carrying a
/// diagnostic instead of being dropped for the sake of plotting.
/// </summary>
public sealed record RawQuantity(string Raw, string Unit, double? NormalizedVolts, string CoordinateBasis, string? Error)
{
    public bool IsConvertible => NormalizedVolts is not null && Error is null;

    public static RawQuantity Unparseable(string raw, string reason) =>
        new(raw, string.Empty, null, "GND", reason);

    /// <summary>Keeps a canonical volts value while remembering its original spelling.</summary>
    public static RawQuantity FromVolts(double volts) =>
        new(volts.ToString("G17", System.Globalization.CultureInfo.InvariantCulture), "V", volts, "GND", null);
}

public static class QuantityParser
{
    private static readonly (string Unit, double Factor)[] Units =
    [
        ("mV", 0.001),
        ("V", 1.0),
    ];

    public static RawQuantity Parse(string? token, string? reference = null)
    {
        var raw = token?.Trim() ?? string.Empty;
        var basis = string.IsNullOrWhiteSpace(reference) ? "GND" : reference.Trim();
        if (raw.Length == 0)
            return RawQuantity.Unparseable(raw, "empty quantity");

        var unit = Units
            .Select(u => (Match: raw.EndsWith(u.Unit, StringComparison.OrdinalIgnoreCase), u.Unit, u.Factor))
            .FirstOrDefault(x => x.Match);
        var numberPart = unit.Match ? raw[..^unit.Unit.Length].Trim() : raw;
        var usedUnit = unit.Match ? unit.Unit : string.Empty;

        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return new RawQuantity(raw, usedUnit, null, basis,
                unit.Match ? $"numeric part '{numberPart}' is not a real number"
                           : $"unrecognised unit or number '{raw}' (accepted: V, mV)");
        }

        if (!unit.Match)
            return new RawQuantity(raw, usedUnit, null, basis, "missing or unsupported unit (accepted: V, mV)");

        return new RawQuantity(raw, usedUnit, value * unit.Factor, basis, null);
    }
}
