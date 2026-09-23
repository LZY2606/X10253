using PowerSeq.Core;
using Xunit;

namespace PowerSeq.Tests;

public class EngineTests
{
    private static RailWaveform RailOf(string rail, IReadOnlyList<(double t, double v)> knots,
        double period = 0.1, (double from, double to)? gap = null)
        => CasePack.Rail(rail, "rail", period, 5, knots, knots[0].t, knots[^1].t, gap);

    [Fact]
    public void Hysteresis_RisingAndFallingUseDifferentThresholds()
    {
        // Rises to 1700 (above rise 1600) at ~1.0, drifts down to 1550 (inside band: no transition),
        // then falls below fall 1500 at ~3.0.
        var rail = RailOf("R", new[] { (0.0, 0.0), (1.0, 1700.0), (2.0, 1550.0), (2.5, 1550.0), (3.0, 1400.0), (4.0, 0.0) });
        var spec = new ThresholdSpec("R", 1600, 1500, 0.2);
        var p = Engine.ClassifyRail(rail, spec);

        var onWindow = p.Windows.First(w => w.State == "on");
        Assert.True(onWindow.FromMs is > 0.9 and < 1.1);
        Assert.True(onWindow.ToMs is > 2.6 and < 2.75);
        // No transition inside the hysteresis band 1500..1600.
        Assert.DoesNotContain(p.Windows, w => w.State == "off" && w.FromMs > 1.5 && w.FromMs < 2.6);
    }

    [Fact]
    public void Glitch_ShortExcursion_IsGlitch_NotViolation()
    {
        // 0.15 ms dip below fall threshold; minStable 0.5 -> glitch.
        var rail = RailOf("R", new[] { (0.0, 0.0), (1.0, 1800.0), (3.0, 1800.0), (3.1, 1300.0), (3.25, 1300.0), (3.3, 1800.0), (5.0, 1800.0) });
        var spec = new ThresholdSpec("R", 1600, 1500, 0.5);
        var p = Engine.ClassifyRail(rail, spec);

        var ex = Assert.Single(p.Excursions);
        Assert.Equal("glitch", ex.Verdict);
        Assert.True(ex.DurationMs < 0.5);
        // State machine never leaves "on" for a real window.
        Assert.DoesNotContain(p.Windows, w => w.State == "off" && w.FromMs > 2.0 && w.ToMs < 4.0);
    }

    [Fact]
    public void Glitch_LongExcursion_IsViolation()
    {
        // Same depth, but 0.8 ms below fall threshold; minStable 0.5 -> violation.
        var rail = RailOf("R", new[] { (0.0, 0.0), (1.0, 1800.0), (3.0, 1800.0), (3.1, 1300.0), (3.9, 1300.0), (4.0, 1800.0), (5.0, 1800.0) });
        var spec = new ThresholdSpec("R", 1600, 1500, 0.5);
        var p = Engine.ClassifyRail(rail, spec);

        var ex = Assert.Single(p.Excursions);
        Assert.Equal("violation", ex.Verdict);
        Assert.True(ex.DurationMs >= 0.5);
        Assert.Contains(p.Windows, w => w.State == "off" && w.FromMs > 3.0 && w.FromMs < 3.2);
    }

    [Fact]
    public void ClockGap_ProducesUnknownWindow_AndDiagnostic()
    {
        var rail = RailOf("R", new[] { (0.0, 0.0), (1.0, 1800.0), (6.0, 1800.0), (7.0, 0.0) }, gap: (3.0, 3.6));
        var spec = new ThresholdSpec("R", 1600, 1500, 0.2);
        var p = Engine.ClassifyRail(rail, spec);

        var unknown = Assert.Single(p.Windows, w => w.State == "unknown");
        Assert.True(unknown.FromMs >= 3.0 && unknown.ToMs <= 3.7);
        Assert.Contains(p.Diagnostics, d => d.Contains("clock gap"));
    }

    [Fact]
    public void UnconvertibleUnit_KeptRaw_WithDiagnostic()
    {
        var rail = CasePack.Rail("RAW", "rail", 0.2, 3, new[] { (0.0, 12.0), (5.0, 400.0) }, 0.0, 5.0, unit: "LSB");
        var spec = new ThresholdSpec("RAW", 300, 250, 0.5);
        var p = Engine.ClassifyRail(rail, spec);

        Assert.Empty(p.Windows);
        Assert.Contains(p.Diagnostics, d => d.Contains("LSB") && d.Contains("kept raw"));
        // Raw samples untouched.
        Assert.Equal(12.0, rail.Samples[0].V);
        Assert.Equal("LSB", rail.ValueUnit);
    }

    [Fact]
    public void SimultaneousArrival_KeepsAllLegalCandidates()
    {
        var g = DependencyGraph.Create(null, "g", "t", "2026-01-01T00:00:00Z",
            new[] { DependencyEdge.Create("A", "B", 0.3, 5.0) });
        var wins = new Dictionary<string, StateWindowPayload>
        {
            ["A"] = new("A", new[] { new StateWindow("A", "off", 0, 1.0, ""), new StateWindow("A", "on", 1.0, 5, "") }, Array.Empty<Excursion>(), Array.Empty<string>()),
            ["B"] = new("B", new[] { new StateWindow("B", "off", 0, 1.02, ""), new StateWindow("B", "on", 1.02, 5, "") }, Array.Empty<Excursion>(), Array.Empty<string>()),
        };
        var report = Engine.CheckSequence(wins, g,
            new Dictionary<string, double> { ["A"] = 0.1, ["B"] = 0.1 },
            new Dictionary<string, double> { ["A"] = 0, ["B"] = 0 });

        var up = report.Checks.Single(c => c.Phase == "up");
        Assert.Equal("ambiguous", up.Status);
        Assert.NotNull(up.Candidates);
        Assert.Equal(2, up.Candidates!.Count);
        Assert.Contains(up.Candidates, c => c.Ordering[0] == "A");
        Assert.Contains(up.Candidates, c => c.Ordering[0] == "B");
    }

    [Fact]
    public void ShutdownReverseOrder_ViolationDetected()
    {
        // Edge A->B: on shutdown A must fall AFTER B (+0.3). Here A falls first.
        var g = DependencyGraph.Create(null, "g", "t", "2026-01-01T00:00:00Z",
            new[] { DependencyEdge.Create("A", "B", 0.3, 5.0) });
        var wins = new Dictionary<string, StateWindowPayload>
        {
            ["A"] = new("A", new[] { new StateWindow("A", "off", 0, 1, ""), new StateWindow("A", "on", 1, 6.0, ""), new StateWindow("A", "off", 6.0, 10, "") }, Array.Empty<Excursion>(), Array.Empty<string>()),
            ["B"] = new("B", new[] { new StateWindow("B", "off", 0, 2, ""), new StateWindow("B", "on", 2, 8.0, ""), new StateWindow("B", "off", 8.0, 10, "") }, Array.Empty<Excursion>(), Array.Empty<string>()),
        };
        var report = Engine.CheckSequence(wins, g,
            new Dictionary<string, double> { ["A"] = 0.1, ["B"] = 0.1 },
            new Dictionary<string, double> { ["A"] = 0, ["B"] = 0 });

        var down = report.Checks.Single(c => c.Phase == "down");
        Assert.Equal("violation", down.Status);
        Assert.Equal(report.FirstViolation, down);
        Assert.Equal(8.0 + 0.3, down.RequiredMs);   // B falls at 8.0, A must fall at >= 8.3
        Assert.Equal(6.0, down.ActualMs);           // A actually falls at 6.0
    }

    [Fact]
    public void CycleDependency_MinimalContradictionEdgeSets_AllKept()
    {
        // Cycles: A->B->C->A and A->B->C->D->A share edges AB, BC.
        var edges = new[]
        {
            DependencyEdge.Create("A", "B", 0.1, 1),
            DependencyEdge.Create("B", "C", 0.1, 1),
            DependencyEdge.Create("C", "A", 0.1, 1),
            DependencyEdge.Create("C", "D", 0.1, 1),
            DependencyEdge.Create("D", "A", 0.1, 1),
        };
        var g = DependencyGraph.Create(null, "g", "t", "2026-01-01T00:00:00Z", edges);
        var report = Engine.CheckCycles(g);

        Assert.True(report.HasCycle);
        Assert.Equal(2, report.Cycles.Count);
        Assert.Equal(2, report.MinimalContradictionEdgeSets.Count);
        var ab = edges[0].Id; var bc = edges[1].Id;
        Assert.Contains(report.MinimalContradictionEdgeSets, s => s.SequenceEqual(new[] { ab }));
        Assert.Contains(report.MinimalContradictionEdgeSets, s => s.SequenceEqual(new[] { bc }));
    }

    [Fact]
    public void AcyclicGraph_NoContradiction()
    {
        var g = DependencyGraph.Create(null, "g", "t", "2026-01-01T00:00:00Z",
            new[] { DependencyEdge.Create("A", "B", 0.1, 1), DependencyEdge.Create("B", "C", 0.1, 1) });
        var report = Engine.CheckCycles(g);
        Assert.False(report.HasCycle);
        Assert.Empty(report.MinimalContradictionEdgeSets);
    }
}
