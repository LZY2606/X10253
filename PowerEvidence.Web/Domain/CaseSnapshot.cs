using System.Text.Json;

namespace PowerEvidence.Domain;

public sealed record CaseSnapshot(
    IReadOnlyList<RailSpec> Rails,
    IReadOnlyList<SignalSpec> Signals,
    IReadOnlyList<DependencySpec> Dependencies,
    IReadOnlyList<ClockSpec> Clocks,
    IReadOnlyList<string> SourceVersions,
    IReadOnlyDictionary<string, string> ActiveSourceVersions,
    JsonElement? BaselineRules);

public static class CaseSnapshotBuilder
{
    public static CaseSnapshot FromProject(ProjectView view)
    {
        var rails = new Dictionary<string, RailSpec>();
        var signals = new Dictionary<string, SignalSpec>();
        var dependencies = new Dictionary<string, DependencySpec>();
        var clocks = new Dictionary<string, ClockSpec>();
        var sourceOrder = new List<string>();
        var active = new Dictionary<string, string>();
        JsonElement? baselineRules = null;

        foreach (var source in view.SourceVersions.OrderBy(v => v.EventOrdinal))
        {
            if (!sourceOrder.Contains(source.VersionId))
                sourceOrder.Add(source.VersionId);

            if (source.Kind == "baseline")
            {
                ReadBaseline(source.Body, source.VersionId);
                active["baseline"] = source.VersionId;
            }
            else if (source.Kind == "source-revision")
            {
                ReadRevision(source.Body, source.VersionId, source.ParentVersionId);
                if (source.Body.TryGetProperty("scope", out var scope))
                    active[scope.GetString() ?? "unknown"] = source.VersionId;
            }
        }

        var firstRules = view.RuleVersions.OrderBy(v => v.EventOrdinal).FirstOrDefault();
        if (firstRules is not null)
            baselineRules = firstRules.Body;

        return new CaseSnapshot(
            rails.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            signals.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
            dependencies.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToList(),
            clocks.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList(),
            sourceOrder,
            active,
            baselineRules);

        void ReadBaseline(JsonElement body, string versionId)
        {
            rails.Clear();
            signals.Clear();
            dependencies.Clear();
            clocks.Clear();

            foreach (var rail in body.GetProperty("rails").EnumerateArray())
            {
                var id = rail.GetProperty("id").GetString()!;
                rails[id] = new RailSpec(id, rail.GetProperty("name").GetString()!, versionId);
            }

            foreach (var clock in body.GetProperty("clocks").EnumerateArray())
            {
                var id = clock.GetProperty("id").GetString()!;
                clocks[id] = new ClockSpec(
                    id,
                    TypedValue.Parse(clock.GetProperty("expectedPeriod"), "time"),
                    TypedValue.Parse(clock.GetProperty("gapLimit"), "duration"),
                    versionId);
            }

            foreach (var signal in body.GetProperty("signals").EnumerateArray())
                AddSignal(signal, versionId, clocks.Keys.FirstOrDefault());

            foreach (var dependency in body.GetProperty("dependencies").EnumerateArray())
                AddDependency(dependency, versionId);
        }

        void ReadRevision(JsonElement body, string versionId, string parentVersionId)
        {
            if (body.TryGetProperty("rails", out var revisedRails))
                foreach (var rail in revisedRails.EnumerateArray())
                {
                    var id = rail.GetProperty("id").GetString()!;
                    rails[id] = new RailSpec(id, rail.GetProperty("name").GetString()!, versionId);
                }

            if (body.TryGetProperty("signals", out var revisedSignals))
                foreach (var signal in revisedSignals.EnumerateArray())
                {
                    var id = signal.GetProperty("id").GetString()!;
                    if (signal.TryGetProperty("deleted", out var deleted) && deleted.GetBoolean())
                        signals.Remove(id);
                    else
                        AddSignal(signal, versionId, signals.GetValueOrDefault(id)?.ClockId);
                }

            if (body.TryGetProperty("dependencies", out var revisedDeps))
                foreach (var dependency in revisedDeps.EnumerateArray())
                {
                    var id = dependency.GetProperty("id").GetString()!;
                    if (dependency.TryGetProperty("deleted", out var deleted) && deleted.GetBoolean())
                        dependencies.Remove(id);
                    else
                        AddDependency(dependency, versionId);
                }

            if (body.TryGetProperty("clocks", out var revisedClocks))
                foreach (var clock in revisedClocks.EnumerateArray())
                {
                    var id = clock.GetProperty("id").GetString()!;
                    clocks[id] = new ClockSpec(
                        id,
                        TypedValue.Parse(clock.GetProperty("expectedPeriod"), "time"),
                        TypedValue.Parse(clock.GetProperty("gapLimit"), "duration"),
                        versionId);
                }
        }
    }

    private static void AddSignal(JsonElement signal, string versionId, string? clockId)
    {
        var id = signal.GetProperty("id").GetString()!;
        var error = signal.TryGetProperty("measurementError", out var e)
            ? TypedValue.Parse(e, "voltage")
            : null;
        var samples = signal.GetProperty("samples").EnumerateArray()
            .Select(sample =>
            {
                var measurementError = sample.TryGetProperty("measurementError", out var me) ? me.GetString() : null;
                return new RawSample(
                    sample.GetProperty("id").GetString()!,
                    TypedValue.Parse(sample.GetProperty("t"), "time"),
                    TypedValue.Parse(sample.GetProperty("v"), "voltage"),
                    measurementError);
            })
            .OrderBy(s => s.Time.Numeric ?? double.MaxValue)
            .ToList();

        signals[id] = new SignalSpec(
            id,
            signal.GetProperty("railId").GetString()!,
            signal.GetProperty("label").GetString() ?? id,
            Enum.Parse<SignalRole>(signal.GetProperty("role").GetString()!, true),
            TypedValue.Parse(signal.GetProperty("riseThreshold"), "voltage"),
            TypedValue.Parse(signal.GetProperty("fallThreshold"), "voltage"),
            TypedValue.Parse(signal.GetProperty("hysteresis"), "voltage"),
            TypedValue.Parse(signal.GetProperty("minimumStable"), "duration"),
            error,
            samples,
            versionId,
            signal.TryGetProperty("clockId", out var c) ? c.GetString() : clockId);
    }

    private static void AddDependency(JsonElement dependency, string versionId)
    {
        var minimum = dependency.TryGetProperty("minimumGap", out var m)
            ? TypedValue.Parse(m, "duration")
            : new TypedValue("0 s", "s", 0, true, "duration", null);
        var phase = dependency.TryGetProperty("phase", out var p)
            ? Enum.Parse<ConstraintPhase>(p.GetString()!, true)
            : ConstraintPhase.Both;
        var simultaneous = dependency.TryGetProperty("allowSimultaneous", out var s) && s.GetBoolean();

        dependencies[dependency.GetProperty("id").GetString()!] = new DependencySpec(
            dependency.GetProperty("id").GetString()!,
            dependency.GetProperty("fromRail").GetString()!,
            dependency.GetProperty("fromSignal").GetString()!,
            dependency.GetProperty("fromEdge").GetString()!,
            dependency.GetProperty("toRail").GetString()!,
            dependency.GetProperty("toSignal").GetString()!,
            dependency.GetProperty("toEdge").GetString()!,
            phase,
            simultaneous,
            minimum,
            versionId);
    }
}
