using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerTiming.Core.Domain;

namespace PowerTiming.Core.Import;

/// <summary>
/// Normalizes domain quantities in the derived layer only. The original
/// spelling is always retained; an unconvertible value stays in place with
/// a diagnostic instead of being dropped for display convenience.
/// </summary>
public static partial class QuantityParser
{
    private static readonly Dictionary<string, double> VoltageFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"] = 1.0,
        ["mV"] = 1e-3,
        ["uV"] = 1e-6,
        ["µV"] = 1e-6
    };

    private static readonly Dictionary<string, double> TimeFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s"] = 1.0,
        ["ms"] = 1e-3,
        ["us"] = 1e-6,
        ["µs"] = 1e-6,
        ["ns"] = 1e-9
    };

    public static RawQuantity Parse(JsonElement? source, string path, string dimension, ICollection<ImportDiagnostic> diagnostics, bool required = true)
    {
        if (source is null || source.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (required)
            {
                diagnostics.Add(new ImportDiagnostic("E_MISSING", path, "Required quantity is missing.", null));
            }

            return new RawQuantity(string.Empty, null, null, null, required ? "missing" : null);
        }

        return Parse(source.Value, path, dimension, diagnostics, required);
    }

    public static RawQuantity Parse(JsonElement source, string path, string dimension, ICollection<ImportDiagnostic> diagnostics, bool required = true)
    {
        string? raw = null;
        string? explicitUnit = null;
        string? basis = null;
        JsonElement numericElement = source;

        switch (source.ValueKind)
        {
            case JsonValueKind.String:
                raw = source.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
                raw = source.GetRawText();
                numericElement = source;
                break;
            case JsonValueKind.Object when source.TryGetProperty("raw", out var rawProperty):
                raw = rawProperty.ValueKind == JsonValueKind.String ? rawProperty.GetString() : rawProperty.GetRawText();
                if (source.TryGetProperty("unit", out var unitProperty) && unitProperty.ValueKind == JsonValueKind.String)
                {
                    explicitUnit = unitProperty.GetString();
                }

                if (source.TryGetProperty("coordinate_basis", out var basisProperty) && basisProperty.ValueKind == JsonValueKind.String)
                {
                    basis = basisProperty.GetString();
                }

                if (source.TryGetProperty("value", out var valueProperty))
                {
                    numericElement = valueProperty;
                }

                break;
            default:
                if (required)
                {
                    diagnostics.Add(new ImportDiagnostic("E_SHAPE", path, "Quantity must be a string, number, or {raw,unit,value}.", source.GetRawText()));
                }

                return new RawQuantity(source.GetRawText(), null, null, null, "unsupported shape");
        }

        var match = QuantityRegex().Match(raw ?? string.Empty);
        if (!match.Success)
        {
            diagnostics.Add(new ImportDiagnostic("E_UNPARSEABLE", path, $"Cannot preserve-and-convert quantity '{raw}'.", raw));
            return new RawQuantity(raw ?? string.Empty, explicitUnit, basis, null, "unparseable");
        }

        var numberText = match.Groups["num"].Value;
        var unit = explicitUnit ?? match.Groups["unit"].Value;
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            diagnostics.Add(new ImportDiagnostic("E_NUMBER", path, $"Numeric part '{numberText}' is not a finite number.", raw));
            return new RawQuantity(raw!, unit, basis, null, "bad number");
        }

        var factors = dimension.Equals("voltage", StringComparison.OrdinalIgnoreCase) ? VoltageFactors : TimeFactors;
        if (!factors.TryGetValue(unit, out var factor))
        {
            diagnostics.Add(new ImportDiagnostic(
                basis is not null ? "W_COORDINATE_UNCONVERTED" : "E_UNIT",
                path,
                basis is not null
                    ? $"Coordinate basis '{basis}' is retained; relative value is not converted to absolute SI here."
                    : $"Unit '{unit}' is not a supported {dimension} unit.",
                raw));

            return new RawQuantity(raw!, unit, basis, null, basis is not null ? "coordinate reference deferred" : "unknown unit");
        }

        if (basis is not null)
        {
            return new RawQuantity(raw!, unit, basis, null, "coordinate reference retained");
        }

        return new RawQuantity(raw!, unit, basis, numeric * factor, null);
    }

    [GeneratedRegex(@"^\s*(?<num>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(?<unit>[a-zA-Zµ]*)\s*$")]
    private static partial Regex QuantityRegex();
}
