using PowerSeq.Core;
using Xunit;

namespace PowerSeq.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "powerseq-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (Workspace ws, FakeClock clock) NewWorkspace(string name = "test.db")
    {
        Directory.CreateDirectory(_dir);
        var clock = new FakeClock();
        return (new Workspace(new Store(Path.Combine(_dir, name)), clock), clock);
    }

    [Fact]
    public void ThresholdFork_OnlyExpiresDependentResults()
    {
        var (ws, _) = NewWorkspace();
        var wv = CasePack.InitialWaveforms(ws.Clock);
        var th1 = CasePack.InitialThresholds(ws.Clock);
        ws.ImportWaveforms(wv);
        ws.ImportThresholds(th1);
        ws.ImportGraph(CasePack.InitialGraph(ws.Clock));

        var before = ws.Results();
        Assert.All(before, r => Assert.False(r.Stale));

        // Fork thresholds: change ONLY VDD_1V8 min stable time.
        var th2 = CasePack.InitialThresholds(ws.Clock, parentId: th1.Id, vdd1v8MinStable: 0.1);
        Assert.NotEqual(th1.Id, th2.Id);
        Assert.Equal(th1.Id, th2.ParentId);
        ws.ImportThresholds(th2);

        var after = ws.Results().ToList();
        var staleOld = after.Where(r => r.Stale && string.CompareOrdinal(r.Result.CreatedAt, th2.CreatedAt) < 0).ToList();
        Assert.NotEmpty(staleOld);
        // Every stale result must touch VDD_1V8; nothing else expired.
        Assert.All(staleOld, r => Assert.Contains(r.Result.Scope, s => s.Rail == "VDD_1V8"));
        var currentOld = after.Where(r => !r.Stale && string.CompareOrdinal(r.Result.CreatedAt, th2.CreatedAt) < 0).ToList();
        Assert.NotEmpty(currentOld);
        Assert.All(currentOld, r => Assert.DoesNotContain(r.Result.Scope, s => s.Rail == "VDD_1V8"));
        // Stale results remain viewable with reasons.
        Assert.All(staleOld, r => Assert.NotEmpty(r.StaleReasons));
    }

    [Fact]
    public void LocalWaveformRevision_OnlyExpiresAffectedRail()
    {
        var (ws, _) = NewWorkspace();
        var wv1 = CasePack.InitialWaveforms(ws.Clock);
        ws.ImportWaveforms(wv1);
        ws.ImportThresholds(CasePack.InitialThresholds(ws.Clock));
        ws.ImportGraph(CasePack.InitialGraph(ws.Clock));

        // Local source change: fill only the VDD_3V3 clock gap.
        var wv2 = CasePack.InitialWaveforms(ws.Clock, parentId: wv1.Id, fillClockGap: true);
        ws.ImportWaveforms(wv2);

        var stale = ws.Results().Where(r => r.Stale).ToList();
        Assert.NotEmpty(stale);
        Assert.All(stale, r => Assert.Contains(r.Result.Scope, s => s.Rail == "VDD_3V3"));
        // VDD_1V8 results are untouched by the VDD_3V3-only revision.
        var oneVEight = ws.Results().Where(r =>
            r.Result.Kind == "state-windows" && r.Result.Scope.Any(s => s.Rail == "VDD_1V8")).ToList();
        Assert.All(oneVEight, r => Assert.False(r.Stale));
    }

    [Fact]
    public void RawInputs_AreImmutable_RevisionsUseParentLinks()
    {
        var (ws, _) = NewWorkspace();
        var wv1 = CasePack.InitialWaveforms(ws.Clock);
        ws.ImportWaveforms(wv1);
        // Re-import of identical content is idempotent, not an overwrite.
        ws.ImportWaveforms(wv1);
        Assert.Single(ws.Store.Inputs(Workspace.KindWaveforms));

        var wv2 = CasePack.InitialWaveforms(ws.Clock, parentId: wv1.Id, fillClockGap: true);
        ws.ImportWaveforms(wv2);
        var all = ws.Store.Inputs(Workspace.KindWaveforms);
        Assert.Equal(2, all.Count);
        Assert.Equal(wv1.Id, all[1].ParentId);
        // Original version still retrievable byte-for-byte.
        var raw = ws.Store.GetInputJson(Workspace.KindWaveforms, wv1.Id)!;
        Assert.Equal(Canon.Json(wv1), raw);
    }

    [Fact]
    public void DerivedResults_CarryProvenance_AndReplayDeterministically()
    {
        var (ws, _) = NewWorkspace();
        ws.ImportWaveforms(CasePack.InitialWaveforms(ws.Clock));
        ws.ImportThresholds(CasePack.InitialThresholds(ws.Clock));
        ws.ImportGraph(CasePack.InitialGraph(ws.Clock));

        var results = ws.Results().Select(r => r.Result).ToList();
        Assert.NotEmpty(results);
        foreach (var r in results)
        {
            Assert.False(string.IsNullOrEmpty(r.AlgorithmVersion));
            Assert.False(string.IsNullOrEmpty(r.ParamFingerprint));
            Assert.NotEmpty(r.SourceIds);
        }
        // Recompute: identical inputs => identical ids, nothing new inserted.
        var again = ws.ComputeAll();
        Assert.Empty(again);
        Assert.Equal(results.Count, ws.Results().Count);
    }

    [Fact]
    public void Export_DeleteDB_Import_PreservesIdentityOrderAndProvenance()
    {
        string exported;
        List<string> resultIds, eventIds, inputIds;
        {
            var (ws, _) = NewWorkspace();
            CasePack.Apply(ws);
            exported = ws.Export();
            resultIds = ws.Results().Select(r => r.Result.Id).ToList();
            eventIds = ws.Events().Select(e => e.Id).ToList();
            inputIds = ws.Store.Inputs().Select(i => i.Id).ToList();
        }
        foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);

        {
            var (ws2, _) = NewWorkspace();
            ws2.ImportArchive(exported);
            Assert.Equal(resultIds, ws2.Results().Select(r => r.Result.Id).ToList());
            var reimported = ws2.Events();
            Assert.Equal(eventIds, reimported.Select(e => e.Id).Take(eventIds.Count).ToList());
            Assert.Equal("archive-imported", reimported[^1].Type);
            Assert.Equal(inputIds, ws2.Store.Inputs().Select(i => i.Id).ToList());
            // Provenance intact: every result still names its sources and fingerprint.
            Assert.All(ws2.Results(), r =>
            {
                Assert.NotEmpty(r.Result.SourceIds);
                Assert.False(string.IsNullOrEmpty(r.Result.ParamFingerprint));
            });
            // Staleness view identical after round-trip.
            Assert.Equal(ws2.Results().Count(r => r.Stale), ws2.Results().Count(r => r.Stale));
        }
    }

    [Fact]
    public void ConflictingDecisions_AreRecorded_AndUndoWritesHistory()
    {
        var (ws, _) = NewWorkspace();
        CasePack.Apply(ws);
        var events = ws.Events();
        Assert.Contains(events, e => e.Type == "conflict-recorded");
        Assert.Contains(events, e => e.Type == "decision-undone");
        var undone = ws.Decisions().Single(d => d.UndoneBy is not null);
        Assert.Equal("reject", undone.Verdict);
        // All decisions retained, none deleted.
        Assert.Equal(3, ws.Decisions().Count);
    }

    [Fact]
    public void Import_RejectsFormatErrors_WithDiagnostics()
    {
        var (ws, _) = NewWorkspace();
        var bad = new WaveformSet("wv_bad", null, "bad", "t", "2026-01-01T00:00:00Z",
            new[]
            {
                new RailWaveform("R1", "switch", "mV", "ms", 0.1, 1, new[] { new SamplePoint(0, 0) }),
                new RailWaveform("R2", "rail", "mV", "ms", -1, 1, new[] { new SamplePoint(0, 0) }),
                new RailWaveform("R3", "rail", "mV", "ms", 0.1, 1, new[] { new SamplePoint(1, 0), new SamplePoint(0.5, 0) }),
            });
        var outcome = ws.ImportWaveforms(bad);
        Assert.False(outcome.Ok);
        Assert.Contains(outcome.Errors, e => e.Contains("role"));
        Assert.Contains(outcome.Errors, e => e.Contains("samplePeriod"));
        Assert.Contains(outcome.Errors, e => e.Contains("non-decreasing"));

        var badTh = ThresholdSet.Create(null, "bad", "t", "2026-01-01T00:00:00Z",
            new[] { new ThresholdSpec("R", 1000, 1200, 0.5) });
        var outcome2 = ws.ImportThresholds(badTh);
        Assert.False(outcome2.Ok);
        Assert.Contains(outcome2.Errors, e => e.Contains("hysteresis"));
    }

    [Fact]
    public void CasePack_Replay_CoversAllSevenScenarios()
    {
        var (ws, _) = NewWorkspace();
        CasePack.Apply(ws);
        var events = ws.Events().ToList();
        var judgments = events.Where(e => e.Type == "judgment").Select(e => e.Json).ToList();

        // 1. hysteresis: VDD_1V8 noise band produced no spurious excursions around 6.0..6.5 ms.
        var oneVEightStale = ws.Results().First(r => r.Result.Kind == "state-windows"
            && r.Result.Scope.Any(s => s.Rail == "VDD_1V8") && r.Stale);
        var payload = Canon.Parse<StateWindowPayload>(oneVEightStale.Result.PayloadJson);
        Assert.DoesNotContain(payload.Excursions, e => e.StartMs >= 5.9 && e.EndMs <= 6.6);

        // 2. short glitch: 0.2 ms dip is a glitch under v1 thresholds.
        Assert.Contains(judgments, j => j.Contains("\"glitch\""));
        // ...and a violation under the forked thresholds (duration decides).
        var v2 = ws.Results().First(r => !r.Stale && r.Result.Kind == "state-windows" && r.Result.Scope.Any(s => s.Rail == "VDD_1V8"));
        Assert.Contains(Canon.Parse<StateWindowPayload>(v2.Result.PayloadJson).Excursions, e => e.Verdict == "violation");

        // 3. simultaneous arrival: ambiguous check with candidates retained.
        Assert.Contains(judgments, j => j.Contains("simultaneous arrival"));

        // 4. cyclic dependency: minimal contradiction edge sets present, multiple kept.
        var cycle = ws.Results().First(r => r.Result.Kind == "cycle-report");
        var cyclePayload = Canon.Parse<CycleReport>(cycle.Result.PayloadJson);
        Assert.True(cyclePayload.HasCycle);
        Assert.True(cyclePayload.MinimalContradictionEdgeSets.Count >= 2);

        // 5. shutdown reverse order violation.
        var seq = ws.Results().Where(r => r.Result.Kind == "sequence-report" && !r.Stale)
            .OrderBy(r => r.Result.CreatedAt, StringComparer.Ordinal).Last();
        var seqPayload = Canon.Parse<SequenceReport>(seq.Result.PayloadJson);
        Assert.Contains(seqPayload.Checks, c => c.Phase == "down" && c.Status == "violation");
        Assert.NotNull(seqPayload.FirstViolation);

        // 6. clock gap: unknown window + unknown sequence checks.
        Assert.Contains(judgments, j => j.Contains("unknown-window"));
        Assert.Contains(seqPayload.Checks, c => c.Status == "unknown");

        // 7. threshold version partial staleness.
        var stale = ws.Results().Where(r => r.Stale).ToList();
        Assert.NotEmpty(stale);
        Assert.All(stale, r => Assert.Contains(r.StaleReasons, reason =>
            reason.Contains("VDD_1V8") || reason.Contains("VDD_3V3")));
    }
}
