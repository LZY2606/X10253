using System.Globalization;
using System.Text;
using PowerSequencer.Domain;

namespace PowerSequencer.Web;

public static class Svg
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Us(double s) => (s * 1e6).ToString("0.##", CultureInfo.InvariantCulture);

    public static string Waveform(
        WaveformData wf, RailConstraint rc, RailAnalysisResult res,
        string? highlightExcursionResult, double? violationT, int w = 920, int h = 250)
    {
        Units.TryNormalize(rc.RiseThreshold, UnitKind.Voltage, out var riseTh, out _);
        Units.TryNormalize(rc.FallThreshold, UnitKind.Voltage, out var fallTh, out _);
        Units.TryNormalize(rc.Hysteresis, UnitKind.Voltage, out var hyst, out _);
        var effRise = riseTh + hyst / 2;
        var effFall = fallTh - hyst / 2;

        var pts = new List<(double t, double v)>();
        foreach (var s in wf.Samples)
            if (Units.TryNormalize(s.Time, UnitKind.Time, out var t, out _) &&
                Units.TryNormalize(s.Value, UnitKind.Voltage, out var v, out _))
                pts.Add((t, v));
        if (pts.Count == 0) return "<p>no convertible samples</p>";

        var (ml, mr, mt, mb) = (56, 20, 18, 34);
        var t0 = pts.Min(p => p.t); var t1 = pts.Max(p => p.t);
        var vMax = Math.Max(pts.Max(p => p.v), effRise) * 1.12;
        double X(double t) => ml + (t - t0) / (t1 - t0 + 1e-12) * (w - ml - mr);
        double Y(double v) => mt + (1 - v / vMax) * (h - mt - mb);

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"{wf.Rail} waveform\">");
        sb.Append("<defs><pattern id=\"gap\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\">" +
                  "<path d=\"M0 8 L8 0\" stroke=\"#c79a18\" stroke-width=\"1.5\"/></pattern>");
        sb.Append("<marker id=\"arrow\" markerWidth=\"9\" markerHeight=\"9\" refX=\"8\" refY=\"3\" orient=\"auto\">" +
                  "<path d=\"M0,0 L8,3 L0,6 z\" fill=\"#b4232a\"/></marker></defs>");
        sb.Append($"<rect x=\"{ml}\" y=\"{mt}\" width=\"{w - ml - mr}\" height=\"{h - mt - mb}\" fill=\"#fff\" stroke=\"#cbd5e1\"/>");

        // hysteresis deadband
        sb.Append($"<rect x=\"{ml}\" y=\"{N(Y(effRise))}\" width=\"{w - ml - mr}\" height=\"{N(Y(effFall) - Y(effRise))}\" fill=\"#fde68a\" fill-opacity=\"0.28\"/>");

        // state windows
        foreach (var win in res.Windows)
        {
            var fill = win.State switch
            {
                RailState.High => "#d8f3dc",
                RailState.Low => "#eef2f7",
                _ => "#fff3cd"
            };
            sb.Append($"<rect x=\"{N(X(win.TStart))}\" y=\"{mt}\" width=\"{N(Math.Max(1, X(win.TEnd) - X(win.TStart)))}\" height=\"{h - mt - mb}\" fill=\"{fill}\"/>");
            if (!win.Certain)
                sb.Append($"<rect x=\"{N(X(win.TStart))}\" y=\"{mt}\" width=\"{N(Math.Max(1, X(win.TEnd) - X(win.TStart)))}\" height=\"{h - mt - mb}\" fill=\"url(#gap)\" fill-opacity=\"0.5\"/>");
        }

        // clock gaps marked on samples directly (Unknown windows cover them, add explicit marker)
        foreach (var d in res.Diagnostics.Where(d => d.Code == "clock-gap"))
        {
            var x = X(d.At ?? 0);
            sb.Append($"<line x1=\"{N(x)}\" y1=\"{mt}\" x2=\"{N(x)}\" y2=\"{h - mb}\" stroke=\"#c79a18\" stroke-dasharray=\"3 3\"/>");
        }

        // thresholds
        sb.Append($"<line x1=\"{ml}\" y1=\"{N(Y(effRise))}\" x2=\"{w - mr}\" y2=\"{N(Y(effRise))}\" stroke=\"#15803d\" stroke-dasharray=\"6 4\"/>");
        sb.Append($"<text x=\"{w - mr - 4}\" y=\"{N(Y(effRise) - 3)}\" text-anchor=\"end\" font-size=\"10\" fill=\"#15803d\">rise {N(riseTh)}V</text>");
        sb.Append($"<line x1=\"{ml}\" y1=\"{N(Y(effFall))}\" x2=\"{w - mr}\" y2=\"{N(Y(effFall))}\" stroke=\"#c2410c\" stroke-dasharray=\"6 4\"/>");
        sb.Append($"<text x=\"{w - mr - 4}\" y=\"{N(Y(effFall) + 11)}\" text-anchor=\"end\" font-size=\"10\" fill=\"#c2410c\">fall {N(fallTh)}V</text>");

        // waveform polyline
        var line = string.Join(" ", pts.Select(p => $"{N(X(p.t))},{N(Y(p.v))}"));
        sb.Append($"<polyline points=\"{line}\" fill=\"none\" stroke=\"#0f4c81\" stroke-width=\"1.6\"/>");
        foreach (var p in pts)
            sb.Append($"<circle cx=\"{N(X(p.t))}\" cy=\"{N(Y(p.v))}\" r=\"1.6\" fill=\"#0f4c81\"/>");

        // excursions
        foreach (var ex in res.Excursions)
        {
            var cx = X((ex.TStart + ex.TEnd) / 2);
            var cy = mt + 16;
            var (color, symbol, label) = ex.Classification switch
            {
                ExcursionClass.Glitch => ("#b45309", "▲", "glitch"),
                ExcursionClass.Violation => ("#b4232a", "■", "violation"),
                ExcursionClass.Ambiguous => ("#7c3aed", "◆", "ambiguous"),
                _ => ("#6b7280", "?", "unknown")
            };
            sb.Append($"<g><text x=\"{N(cx)}\" y=\"{cy}\" font-size=\"14\" text-anchor=\"middle\" fill=\"{color}\">{symbol}</text>" +
                      $"<text x=\"{N(cx)}\" y=\"{cy + 12}\" font-size=\"9\" text-anchor=\"middle\" fill=\"{color}\">{label} {Us(ex.Duration)}us</text></g>");
        }

        // first violation arrow
        if (violationT is not null && violationT >= t0 && violationT <= t1)
        {
            var x = X(violationT.Value);
            sb.Append($"<line x1=\"{N(x)}\" y1=\"{h - mb + 2}\" x2=\"{N(x)}\" y2=\"{mt + 30}\" stroke=\"#b4232a\" stroke-width=\"2\" marker-end=\"url(#arrow)\"/>");
        }

        // axes labels
        for (var i = 0; i <= 4; i++)
        {
            var t = t0 + (t1 - t0) * i / 4;
            var v = vMax * (1 - (double)i / 4);
            sb.Append($"<text x=\"{ml - 5}\" y=\"{N(Y(v) + 3)}\" text-anchor=\"end\" font-size=\"9\" fill=\"#475569\">{N(v)}V</text>");
            sb.Append($"<text x=\"{N(ml + (w - ml - mr) * i / 4)}\" y=\"{h - mb + 14}\" text-anchor=\"middle\" font-size=\"9\" fill=\"#475569\">{Us(t)}us</text>");
        }
        sb.Append($"<text x=\"{ml}\" y=\"{h - 2}\" font-size=\"10\" fill=\"#334155\">{wf.Rail} ({wf.SignalKind}) — id {res.Rail} result provenance via trace link</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string Graph(ConstraintSet cs, GraphAnalysisResult graph, int w = 920, int h = 300)
    {
        var nodes = cs.Rails.Select(r => r.Rail)
            .Concat(cs.Edges.SelectMany(e => new[] { e.From, e.To }))
            .Distinct(StringComparer.Ordinal).ToList();
        var removed = graph.Cycles?.MinimalEdgeSets.FirstOrDefault() ?? new List<DependencyEdge>();
        var cycleEdges = graph.Cycles?.MinimalEdgeSets.SelectMany(s => s)
            .ToHashSet(EqualityComparer<DependencyEdge>.Create(
                (a, b) => a!.From == b!.From && a.To == b.To,
                e => HashCode.Combine(e.From, e.To))) ?? new HashSet<DependencyEdge>();
        var adj = nodes.ToDictionary(n => n, _ => new List<string>());
        foreach (var e in cs.Edges.Except(removed))
            if (nodes.Contains(e.From) && nodes.Contains(e.To)) adj[e.From].Add(e.To);
        var rank = nodes.ToDictionary(n => n, _ => 0);
        var order = new List<string>();
        var indeg = nodes.ToDictionary(n => n, n => adj[n].Count == 0 ? 0 : 0);
        indeg = nodes.ToDictionary(n => n, n => cs.Edges.Except(removed).Count(e => e.To == n && nodes.Contains(e.From)));
        var q = new Queue<string>(nodes.Where(n => indeg[n] == 0));
        while (q.Count > 0)
        {
            var n = q.Dequeue(); order.Add(n);
            foreach (var m in adj[n])
            {
                rank[m] = Math.Max(rank[m], rank[n] + 1);
                if (--indeg[m] == 0) q.Enqueue(m);
            }
        }
        foreach (var n in nodes.Except(order)) { rank[n] = 0; order.Add(n); }
        var maxRank = Math.Max(1, rank.Values.Max());

        double Xn(string n) => 40 + (maxRank == 0 ? 0 : (double)rank[n] / maxRank) * (w - 160) + 30;
        var byRank = order.GroupBy(n => rank[n]).OrderBy(g => g.Key).ToList();
        var pos = new Dictionary<string, (double x, double y)>();
        foreach (var g in byRank)
        {
            var arr = g.ToList();
            for (var i = 0; i < arr.Count; i++)
                pos[arr[i]] = (Xn(arr[i]), 40 + i * 70 + (h - 80) / (arr.Count + 1));
        }

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"dependency graph\">");
        sb.Append("<defs><marker id=\"ga\" markerWidth=\"9\" markerHeight=\"9\" refX=\"8\" refY=\"3\" orient=\"auto\">" +
                  "<path d=\"M0,0 L8,3 L0,6 z\" fill=\"#64748b\"/></marker>");
        sb.Append("<marker id=\"gr\" markerWidth=\"9\" markerHeight=\"9\" refX=\"8\" refY=\"3\" orient=\"auto\">" +
                  "<path d=\"M0,0 L8,3 L0,6 z\" fill=\"#b4232a\"/></marker></defs>");
        foreach (var e in cs.Edges)
        {
            if (!pos.ContainsKey(e.From) || !pos.ContainsKey(e.To)) continue;
            var verdict = graph.Verdicts.FirstOrDefault(v => v.Edge.From == e.From && v.Edge.To == e.To);
            var (a, b) = (pos[e.From], pos[e.To]);
            var color = verdict?.Verdict switch
            {
                "satisfied" => "#15803d",
                "violated" => "#b4232a",
                "ambiguous" => "#b45309",
                _ => "#94a3b8"
            };
            var inCycle = cycleEdges.Contains(e);
            var dash = inCycle ? "6 4" : "none";
            var marker = inCycle || verdict?.Verdict == "violated" ? "gr" : "ga";
            sb.Append($"<line x1=\"{N(a.x)}\" y1=\"{N(a.y)}\" x2=\"{N(b.x)}\" y2=\"{N(b.y)}\" stroke=\"{color}\" stroke-width=\"{(inCycle ? 2.4 : 1.6)}\" stroke-dasharray=\"{dash}\" marker-end=\"url(#{marker})\"/>");
        }
        foreach (var n in order)
        {
            var (x, y) = pos[n];
            sb.Append($"<rect x=\"{N(x - 46)}\" y=\"{N(y - 16)}\" width=\"92\" height=\"32\" rx=\"6\" fill=\"#fff\" stroke=\"#334155\"/>");
            sb.Append($"<text x=\"{N(x)}\" y=\"{N(y + 4)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#0f172a\">{H(n)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
}
