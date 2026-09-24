using PowerSequencer.Domain;
using PowerSequencer.Persistence;
using System.Text.Json;

namespace PowerSequencer.App;

/// <summary>
/// The offline case pack "电源次序证据室": rail windows, dependency
/// constraints and startup counterexamples. Built deterministically through
/// the same Recorder pipeline the UI uses, then exported as an archive.
/// Narrative: import waveforms + enable/good + constraints v1 -> review ->
/// parameter fork (v2, VDD_IO only) -> local source revision (VDD_CORE clock
/// gap filled) -> two conflicting human decisions.
/// </summary>
public static class CasePack
{
    public const string Project = "power-sequencing-evidence-room";

    private static Quantity Us(double v) => new(v, "us");
    private static Quantity V(double v) => new(v, "V");
    private static Quantity MV(double v) => new(v, "mV");

    /// <summary>Regular samples every 10us with piecewise-defined voltage.</summary>
    private static List<RawSample> Gen(double endUs, Func<double, double?> vAt, bool millivolts = false)
    {
        var pts = new List<RawSample>();
        for (var t = 0.0; t <= endUs + 1e-9; t += 10.0)
        {
            var v = vAt(t);
            if (v is null) continue; // clock gap: sample missing
            pts.Add(new RawSample(Us(t), millivolts ? MV(v.Value) : V(v.Value)));
        }
        return pts;
    }

    private static double Ramp(double t, double t0, double t1, double v0, double v1) =>
        t <= t0 ? v0 : t >= t1 ? v1 : v0 + (v1 - v0) * (t - t0) / (t1 - t0);

    /// <summary>0.9V core rail; clock gap 40-60us mid-rise; one unconvertible sample.</summary>
    public static WaveformData VddCoreV1()
    {
        var pts = Gen(880, t => t switch
        {
            >= 40 and <= 60 => null,               // clock gap
            <= 80 => Ramp(t, 0, 80, 0, 900),       // rise (mV)
            <= 770 => 900,                          // stable
            _ => Ramp(t, 770, 800, 900, 0)          // shutdown fall: Low at 780
        }, millivolts: true);
        pts.Insert(1, new RawSample(Us(5), new Quantity(50, "counts"))); // unconvertible, kept raw
        return new WaveformData("VDD_CORE", "voltage", pts, Us(10), Us(0.5), MV(15));
    }

    /// <summary>Revision: same capture with the clock-gap segment filled in.</summary>
    public static WaveformData VddCoreV2()
    {
        var v1 = VddCoreV1();
        var pts = v1.Samples.Where(s => s.Value.Unit == "counts" || true).ToList();
        var fill = new[]
        {
            new RawSample(Us(40), MV(450)), new RawSample(Us(50), MV(600)), new RawSample(Us(60), MV(700))
        };
        pts.AddRange(fill);
        pts = pts.OrderBy(s => s.Time.Value).ToList();
        return v1 with { Samples = pts };
    }

    /// <summary>1.8V IO rail: hysteresis ripple, glitch, ambiguous dip, violation dip.</summary>
    public static WaveformData VddIo()
    {
        var pts = Gen(880, t => t switch
        {
            <= 40 => Ramp(t, 0, 40, 0, 1.8),        // rise: High at 40
            >= 50 and <= 90 => new[] { 1.8, 1.68, 1.66, 1.69, 1.8 }[(int)((t - 50) / 10)], // hysteresis ripple
            >= 110 and <= 120 => 1.5,               // glitch: 20us dip
            >= 210 and <= 250 => 1.5,               // ambiguous: 50us dip ~= min stable
            >= 310 and <= 400 => 1.5,               // violation: 100us dip
            <= 840 => 1.8,
            _ => Ramp(t, 840, 870, 1.8, 0)          // shutdown fall: Low at 850
        });
        return new WaveformData("VDD_IO", "voltage", pts, Us(10), Us(0.5), MV(15));
    }

    /// <summary>3.3V aux rail: clean rise, shutdown falls after CORE (reverse-order violation).</summary>
    public static WaveformData VddAux()
    {
        var pts = Gen(880, t => t switch
        {
            <= 120 => Ramp(t, 60, 120, 0, 3.3),     // rise: High at 120
            <= 810 => 3.3,
            _ => Ramp(t, 810, 840, 3.3, 0)          // shutdown fall: Low at 820
        });
        return new WaveformData("VDD_AUX", "voltage", pts, Us(10), Us(0.5), MV(15));
    }

    /// <summary>Enable signal for the IO rail; rises 5us after PG_CORE (simultaneous within tolerance).</summary>
    public static WaveformData EnIo()
    {
        var pts = new List<RawSample>();
        for (var t = 0.0; t <= 200; t += 5)
            pts.Add(new RawSample(Us(t), V(t < 50 ? 0.0 : 1.8)));
        return new WaveformData("EN_IO", "enable", pts, Us(5), Us(0.5), MV(10));
    }

    /// <summary>Power-good of the core rail; rises at 45us.</summary>
    public static WaveformData PgCore()
    {
        var pts = new List<RawSample>();
        for (var t = 0.0; t <= 200; t += 5)
            pts.Add(new RawSample(Us(t), V(t < 45 ? 0.0 : 1.8)));
        return new WaveformData("PG_CORE", "good", pts, Us(5), Us(0.5), MV(10));
    }

    public static ConstraintSet ConstraintsV1() => new(
        new List<RailConstraint>
        {
            new("VDD_CORE", MV(810), MV(720), MV(20), Us(50)),
            new("VDD_IO", V(1.71), V(1.62), MV(20), Us(50)),
            new("VDD_AUX", V(3.135), V(2.97), MV(30), Us(50)),
            new("EN_IO", V(0.9), V(0.45), MV(100), Us(20)),
            new("PG_CORE", V(0.9), V(0.45), MV(100), Us(20)),
        },
        new List<DependencyEdge>
        {
            new("VDD_CORE", "VDD_IO", Graph.KindRequiresGoodBeforeEnable, null),
            new("VDD_IO", "VDD_AUX", Graph.KindRequiresGoodBeforeEnable, null),
            new("VDD_AUX", "VDD_CORE", Graph.KindRequiresGoodBeforeEnable, null), // closes the cycle
            new("PG_CORE", "EN_IO", Graph.KindRequiresGoodBeforeEnable, null),
        });

    /// <summary>Parameter revision: only VDD_IO thresholds/min-stable change.</summary>
    public static ConstraintSet ConstraintsV2()
    {
        var v1 = ConstraintsV1();
        var rails = v1.Rails.Select(rc => rc.Rail == "VDD_IO"
            ? rc with { RiseThreshold = V(1.70), MinStableTime = Us(15) }
            : rc).ToList();
        return v1 with { Rails = rails };
    }

    /// <summary>Record the full narrative as events. Deterministic with a ManualClock.</summary>
    public static void Build(Store store, string project, ManualClock clock)
    {
        var rec = new Recorder(store, project);
        var step = TimeSpan.FromMinutes(1);

        rec.ImportWaveform(VddCoreV1()); clock.Advance(step);
        rec.ImportWaveform(VddIo()); clock.Advance(step);
        rec.ImportWaveform(VddAux()); clock.Advance(step);
        rec.ImportWaveform(EnIo()); clock.Advance(step);
        rec.ImportWaveform(PgCore()); clock.Advance(step);
        rec.CreateConstraints(ConstraintsV1(), "cs-seq"); clock.Advance(step);

        // Reviewer A accepts the VDD_IO glitch classification (20us < 50us min stable).
        var proj = store.Project(project);
        var ioRail = proj.Results.First(r => r.Kind == "rail-analysis" && r.Rail == "VDD_IO");
        rec.Decide(ioRail.Id, "accept", "reviewer-A: 20us dip < 50us min stable; glitch confirmed"); clock.Advance(step);

        // Parameter fork: only VDD_IO thresholds change -> partial staleness.
        rec.ForkConstraints("cs-seq", ConstraintsV2()); clock.Advance(step);

        // Local source revision: VDD_CORE clock gap filled.
        var coreStream = proj.Inputs.First(i => i.Kind == "voltage" &&
            i.Data.Deserialize<WaveformData>(Store.JsonOptions)!.Rail == "VDD_CORE").StreamId;
        rec.ReviseWaveform(coreStream, VddCoreV2()); clock.Advance(step);

        // Reviewer B rejects the same target: conflict, both kept, B adopted.
        rec.Decide(ioRail.Id, "reject", "reviewer-B: under v2 min-stable 15us the dip is a violation"); clock.Advance(step);
    }

    /// <summary>Generate the archive JSON deterministically (fixed clock, temp db).</summary>
    public static string GenerateArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"casepack-gen-{Guid.NewGuid():N}.db");
        try
        {
            var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
            using var store = new Store(path, clock);
            Build(store, Project, clock);
            return store.ExportJson(Project);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
