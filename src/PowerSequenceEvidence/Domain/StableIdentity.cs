using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerSequenceEvidence.Domain;

public static class StableIdentity
{
    public const string AlgorithmVersion = "rail-state-v1.3.0";

    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    public static string Short(string prefix, string text) =>
        $"{prefix}_{Sha256(text)[..16]}";

    public static string Fingerprint(EvidenceCase evidence, string caseId, IReadOnlyDictionary<string, string>? activeThresholdVersions = null)
    {
        var selectedThresholds = evidence.Thresholds.Values.AsEnumerable();
        if (activeThresholdVersions is not null)
            selectedThresholds = evidence.Thresholds
                .Where(kv => activeThresholdVersions.TryGetValue(kv.Value.RailId, out var version) && kv.Value.Version == version)
                .Select(kv => kv.Value);
        var orderedThresholds = selectedThresholds
            .OrderBy(t => t.RailId, StringComparer.Ordinal)
            .ThenBy(t => t.Version, StringComparer.Ordinal)
            .Select(t => $"{t.RailId}|{t.Version}|{t.RisingVolts.ToString("R")}|{t.FallingVolts.ToString("R")}|{t.HysteresisVolts.ToString("R")}|{t.MinimumStableSeconds.ToString("R")}");
        var orderedEdges = evidence.Graph.Edges
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => $"{e.Id}|{e.FromRail}>{e.ToRail}|{e.StartupMaxDelaySeconds.ToString("R")}|{e.ShutdownMaxDelaySeconds.ToString("R")}");
        var clocks = $"{evidence.Clock.NominalPeriodSeconds.ToString("R")}|{evidence.Clock.MaxGapSeconds.ToString("R")}";
        var errors = $"{evidence.MeasurementError.VoltageAbsoluteVolts.ToString("R")}|{evidence.MeasurementError.TimeAbsoluteSeconds.ToString("R")}";
        var hashes = evidence.Waveforms.Values.OrderBy(w => w.RailId).Select(w =>
            $"{w.RailId}:{Sha256(string.Join(";", w.Samples.Select(s => $"{s.TimeRaw}={s.VoltageRaw}")))}");
        return Short("fp", $"{caseId}|{string.Join(";", orderedThresholds)}|{string.Join(";", orderedEdges)}|{clocks}|{errors}|{string.Join(";", hashes)}");
    }

    public static string Canonical(JsonDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteElement(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(element.ToString());
                break;
        }
    }
}
