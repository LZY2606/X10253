using System.Globalization;
using System.Text;
using PowerSeq.Core;

namespace PowerSeq.Web;

public static class Svg
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Rail waveform with raw samples, rise/fall threshold lines, state windows and excursions.</summary>
    public static string Waveform(RailWaveform rail, ThresholdSpec? spec, StateWindowPayload? payload)
    {
        const int w = 940, h = 120, padL = 70, padR = 10, padT = 18, padB = 18;
        var sb = new StringBuilder();
        var samples = rail.Samples;
        if (samples.Count == 0) return "";
        var t0 = samples.Min(s => s.T);
        var t1 = samples.Max(s => s.T);
        var factor = Engine.UnitFactorToMv(rail.ValueUnit);
        var vals = samples.Select(s => s.V * (factor ?? 1.0)).ToList();
        var vMax = Math.Max(vals.Max(), spec?.RiseThresholdMv ?? 0) * 1.15 + 1;
        var vMin = Math.Min(0, vals.Min());

        double X(double t) => padL + (t - t0) / Math.Max(t1 - t0, 1e-9) * (w - padL - padR);
        double Y(double v) => padT + (1 - (v - vMin) / (vMax - vMin)) * (h - padT - padB);

        sb.Append($"<svg viewBox='0 0 {w} {h}' class='wave' role='img' aria-label='{Esc(rail.Rail)} waveform'>");
        // state windows as background spans
        if (payload is not null)
            foreach (var win in payload.Windows)
            {
                var fill = win.State switch { "on" => "#d8f3dc", "off" => "#eeeeee", _ => "#fff3bf" };
                sb.Append($"<rect x='{N(X(win.FromMs))}' y='{padT}' width='{N(Math.Max(X(win.ToMs) - X(win.FromMs), 1))}' height='{h - padT - padB}' fill='{fill}'><title>{Esc(win.State)} {N(win.FromMs)}..{N(win.ToMs)} ms — {Esc(win.Basis)}</title></rect>");
            }
        // threshold lines (rising vs falling)
        if (spec is not null && factor is not null)
        {
            sb.Append($"<line x1='{padL}' x2='{w - padR}' y1='{N(Y(spec.RiseThresholdMv))}' y2='{N(Y(spec.RiseThresholdMv))}' stroke='#2d6a4f' stroke-dasharray='5 3'><title>rise threshold {N(spec.RiseThresholdMv)} mV</title></line>");
            sb.Append($"<line x1='{padL}' x2='{w - padR}' y1='{N(Y(spec.FallThresholdMv))}' y2='{N(Y(spec.FallThresholdMv))}' stroke='#9d0208' stroke-dasharray='2 3'><title>fall threshold {N(spec.FallThresholdMv)} mV</title></line>");
            sb.Append($"<rect x='{padL}' y='{N(Y(spec.RiseThresholdMv))}' width='{w - padL - padR}' height='{N(Y(spec.FallThresholdMv) - Y(spec.RiseThresholdMv))}' fill='#2d6a4f' opacity='0.07'><title>hysteresis band {N(spec.FallThresholdMv)}..{N(spec.RiseThresholdMv)} mV</title></rect>");
        }
        // raw samples polyline
        var pts = string.Join(' ', samples.Select((s, i) => $"{N(X(s.T))},{N(Y(vals[i]))}"));
        sb.Append($"<polyline points='{pts}' fill='none' stroke='#1d3557' stroke-width='1.5'/>");
        // excursions
        if (payload is not null)
            foreach (var ex in payload.Excursions)
            {
                var color = ex.Verdict == "glitch" ? "#f4a261" : "#e63946";
                sb.Append($"<rect x='{N(X(ex.StartMs))}' y='{padT}' width='{N(Math.Max(X(ex.EndMs) - X(ex.StartMs), 2))}' height='{h - padT - padB}' fill='{color}' opacity='0.45'><title>{ex.Verdict}: {N(ex.DurationMs)} ms — {Esc(ex.Basis)}</title></rect>");
            }
        sb.Append($"<text x='4' y='{padT + 4}' font-size='11'>{Esc(rail.Rail)} ({Esc(rail.Role)})</text>");
        sb.Append($"<text x='4' y='{padT + 16}' font-size='9' fill='#666'>{Esc(rail.ValueUnit)}/{Esc(rail.TimeUnit)} T={N(rail.SamplePeriod)} err=±{N(rail.ValueError)}</text>");
        if (factor is null)
            sb.Append($"<text x='{padL}' y='{h / 2}' font-size='11' fill='#9d0208'>unit '{Esc(rail.ValueUnit)}' not convertible — raw samples shown, excluded from classification</text>");
        sb.Append($"<text x='{w - padR - 60}' y='{h - 4}' font-size='9' fill='#666'>{N(t0)}..{N(t1)} {Esc(rail.TimeUnit)}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Dependency graph with violation/ambiguous/unknown edge states and cycle highlighting.</summary>
    public static string DependencyGraph(DependencyGraph graph, SequenceReport? seq, CycleReport? cycles)
    {
        const int nodeW = 110, nodeH = 30, gapX = 170, gapY = 56, pad = 30;
        var nodes = graph.Edges.SelectMany(e => new[] { e.Before, e.After }).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        // longest-path layering, cycle-safe
        var layer = nodes.ToDictionary(n => n, _ => 0);
        for (int it = 0; it < nodes.Count; it++)
            foreach (var e in graph.Edges)
                if (layer[e.After] < layer[e.Before] + 1 && layer[e.Before] + 1 <= nodes.Count)
                    layer[e.After] = Math.Min(layer[e.Before] + 1, nodes.Count - 1);
        var perLayer = nodes.GroupBy(n => layer[n]).ToDictionary(g => g.Key, g => g.ToList());
        var pos = new Dictionary<string, (double x, double y)>();
        foreach (var (lay, ns) in perLayer)
            for (int i = 0; i < ns.Count; i++)
                pos[ns[i]] = (pad + lay * gapX, pad + i * gapY);

        var width = pad * 2 + (perLayer.Keys.DefaultIfEmpty(0).Max() + 1) * gapX;
        var height = pad * 2 + perLayer.Values.DefaultIfEmpty(new List<string>()).Max(v => v.Count) * gapY;
        var contradiction = cycles?.MinimalContradictionEdgeSets.FirstOrDefault()?.ToHashSet() ?? new HashSet<string>();
        var checkByEdge = seq?.Checks.GroupBy(c => c.EdgeId).ToDictionary(g => g.Key, g => g.ToList()) ?? new();

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox='0 0 {width} {height}' class='graph' role='img' aria-label='dependency graph'>");
        sb.Append("<defs><marker id='arr' markerWidth='8' markerHeight='8' refX='7' refY='3' orient='auto'><path d='M0,0 L7,3 L0,6 z' fill='#555'/></marker></defs>");
        foreach (var e in graph.Edges)
        {
            var (x1, y1) = pos[e.Before];
            var (x2, y2) = pos[e.After];
            var status = checkByEdge.TryGetValue(e.Id, out var cs) && cs.Any(c => c.Status == "violation") ? "violation"
                : checkByEdge.TryGetValue(e.Id, out var cs2) && cs2.Any(c => c.Status == "ambiguous") ? "ambiguous"
                : checkByEdge.TryGetValue(e.Id, out var cs3) && cs3.All(c => c.Status == "unknown") ? "unknown" : "pass";
            var color = status switch { "violation" => "#e63946", "ambiguous" => "#f4a261", "unknown" => "#999999", _ => "#555555" };
            var dash = contradiction.Contains(e.Id) ? " stroke-dasharray='6 3' stroke-width='3'" : "";
            var title = $"{e.Before} -> {e.After} [{e.MinDelayMs}..{e.MaxDelayMs} ms] {status}"
                + (contradiction.Contains(e.Id) ? " — in a minimal contradiction edge set" : "");
            sb.Append($"<line x1='{N(x1 + nodeW)}' y1='{N(y1 + nodeH / 2)}' x2='{N(x2)}' y2='{N(y2 + nodeH / 2)}' stroke='{color}' stroke-width='1.6'{dash} marker-end='url(#arr)'><title>{Esc(title)}</title></line>");
        }
        foreach (var n in nodes)
        {
            var (x, y) = pos[n];
            sb.Append($"<rect x='{N(x)}' y='{N(y)}' width='{nodeW}' height='{nodeH}' rx='5' fill='#eef2ff' stroke='#3a506b'/>");
            sb.Append($"<text x='{N(x + nodeW / 2)}' y='{N(y + nodeH / 2 + 4)}' font-size='11' text-anchor='middle'>{Esc(n)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
