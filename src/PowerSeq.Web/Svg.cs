using System.Globalization;
using System.Text;
using PowerSeq.Core;

namespace PowerSeq.Web;

/// <summary>SVG 渲染：轨压波形、状态窗口、依赖图。全部服务端生成，无外部依赖。</summary>
public static class Svg
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string RailChart(
        string rail,
        IReadOnlyList<Sample> samples,
        RailTimeline? timeline,
        RailThreshold? threshold,
        ClockSpec clock,
        IReadOnlyList<Violation> violations)
    {
        const int w = 920, h = 200, padL = 60, padR = 16, padT = 14, stripH = 26;
        var plotH = h - padT - stripH - 8;
        if (samples.Count == 0)
            return $"<svg width=\"{w}\" height=\"40\"><text x=\"10\" y=\"20\" fill=\"#888\">no samples</text></svg>";

        var tMin = samples.Min(s => s.TimeNs);
        var tMax = samples.Max(s => s.TimeNs);
        if (tMax <= tMin) tMax = tMin + 1;
        var vMax = Math.Max(samples.Max(s => s.Volts), threshold?.RiseThresholdV ?? 0) * 1.15;
        if (vMax <= 0) vMax = 1;

        double X(double t) => padL + (t - tMin) / (tMax - tMin) * (w - padL - padR);
        double Y(double v) => padT + plotH - v / vMax * plotH;

        var b = new StringBuilder();
        b.Append($"<svg width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" font-family=\"monospace\" font-size=\"11\">");
        b.Append($"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"#fff\" stroke=\"#ccc\"/>");

        // 时钟缺口阴影
        if (timeline is not null)
            foreach (var e in timeline.Events.Where(e => e.Kind == "gap"))
                b.Append($"<rect x=\"{F(X(e.AtNs))}\" y=\"{padT}\" width=\"{F(Math.Max(2, X(e.AtNs + e.DurationNs) - X(e.AtNs)))}\" height=\"{plotH}\" fill=\"#999\" opacity=\"0.25\"/>");

        // 阈值线：上升（绿）与下降（橙）使用不同阈值
        if (threshold is not null)
        {
            b.Append($"<line x1=\"{padL}\" y1=\"{F(Y(threshold.RiseThresholdV))}\" x2=\"{w - padR}\" y2=\"{F(Y(threshold.RiseThresholdV))}\" stroke=\"#2a2\" stroke-dasharray=\"6 3\"/>");
            b.Append($"<text x=\"4\" y=\"{F(Y(threshold.RiseThresholdV) + 3)}\" fill=\"#2a2\">rise {F(threshold.RiseThresholdV)}V</text>");
            b.Append($"<line x1=\"{padL}\" y1=\"{F(Y(threshold.FallThresholdV))}\" x2=\"{w - padR}\" y2=\"{F(Y(threshold.FallThresholdV))}\" stroke=\"#e80\" stroke-dasharray=\"6 3\"/>");
            b.Append($"<text x=\"4\" y=\"{F(Y(threshold.FallThresholdV) + 3)}\" fill=\"#e80\">fall {F(threshold.FallThresholdV)}V</text>");
        }

        // 波形
        var pts = string.Join(" ", samples.Select(s => $"{F(X(s.TimeNs))},{F(Y(s.Volts))}"));
        b.Append($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"#247\" stroke-width=\"1.6\"/>");
        foreach (var s in samples)
            b.Append($"<circle cx=\"{F(X(s.TimeNs))}\" cy=\"{F(Y(s.Volts))}\" r=\"1.6\" fill=\"#247\"/>");

        // glitch 标记（品红三角）
        if (timeline is not null)
            foreach (var e in timeline.Events.Where(e => e.Kind == "glitch"))
            {
                var gx = X(e.AtNs);
                b.Append($"<polygon points=\"{F(gx)},{padT + 2} {F(gx - 5)},{padT + 12} {F(gx + 5)},{padT + 12}\" fill=\"#c2c\"/>");
                b.Append($"<title>glitch @{F(e.AtNs)}ns {System.Net.WebUtility.HtmlEncode(e.Detail)}</title>");
            }

        // 违约标记（红色竖线）
        foreach (var v in violations)
        {
            var vx = X(v.AtNs);
            b.Append($"<line x1=\"{F(vx)}\" y1=\"{padT}\" x2=\"{F(vx)}\" y2=\"{padT + plotH}\" stroke=\"#d00\" stroke-width=\"2\"/>");
            b.Append($"<text x=\"{F(vx + 3)}\" y=\"{padT + 10}\" fill=\"#d00\">✕ {System.Net.WebUtility.HtmlEncode(v.Kind)}</text>");
        }

        // 状态窗口条
        var stripY = h - stripH - 2;
        if (timeline is not null)
        {
            foreach (var win in timeline.Windows)
            {
                var color = win.State switch
                {
                    RailState.Good => "#bfe8bf",
                    RailState.Bad => "#f0c4c4",
                    _ => "#ddd"
                };
                var x1 = X(Math.Max(win.StartNs, tMin));
                var x2 = X(Math.Min(win.EndNs, tMax));
                b.Append($"<rect x=\"{F(x1)}\" y=\"{stripY}\" width=\"{F(Math.Max(1, x2 - x1))}\" height=\"{stripH - 4}\" fill=\"{color}\" stroke=\"#aaa\" stroke-width=\"0.4\"/>");
            }
            b.Append($"<text x=\"4\" y=\"{stripY + 14}\" fill=\"#555\">state</text>");
        }
        b.Append($"<text x=\"{w - padR - 220}\" y=\"{h - 6}\" fill=\"#777\" text-anchor=\"start\">t: {F(tMin)}–{F(tMax)} ns · {rail}</text>");
        b.Append("</svg>");
        return b.ToString();
    }

    public static string DependencyGraph(IReadOnlyList<DependencyEdge> edges, CycleAnalysis? cycle)
    {
        const int w = 560, h = 320, cx = w / 2, cy = h / 2, r = 110;
        var nodes = edges.SelectMany(e => new[] { e.FromRail, e.ToRail }).Distinct().OrderBy(x => x).ToList();
        if (nodes.Count == 0) return "<svg width=\"560\" height=\"40\"><text x=\"10\" y=\"20\" fill=\"#888\">no dependencies</text></svg>";

        var pos = nodes.Select((n, i) =>
        {
            var a = -Math.PI / 2 + 2 * Math.PI * i / nodes.Count;
            return (n, x: cx + r * Math.Cos(a), y: cy + r * Math.Sin(a));
        }).ToDictionary(p => p.n, p => (p.x, p.y));

        var conflictEdges = cycle?.MinimumConflictEdgeSets.SelectMany(s => s).Distinct().ToHashSet()
            ?? new HashSet<DependencyEdge>();

        var b = new StringBuilder();
        b.Append($"<svg width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" font-family=\"monospace\" font-size=\"12\">");
        b.Append("<defs><marker id=\"arr\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 z\" fill=\"#555\"/></marker>");
        b.Append("<marker id=\"arrR\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 z\" fill=\"#d00\"/></marker></defs>");
        b.Append($"<rect width=\"{w}\" height=\"{h}\" fill=\"#fff\" stroke=\"#ccc\"/>");
        foreach (var e in edges)
        {
            var (x1, y1) = pos[e.FromRail];
            var (x2, y2) = pos[e.ToRail];
            var dx = x2 - x1; var dy = y2 - y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var sx = x1 + dx / len * 34; var sy = y1 + dy / len * 34;
            var ex = x1 + dx / len * (len - 38); var ey = y1 + dy / len * (len - 38);
            var conflict = conflictEdges.Contains(e);
            b.Append($"<line x1=\"{F(sx)}\" y1=\"{F(sy)}\" x2=\"{F(ex)}\" y2=\"{F(ey)}\" stroke=\"{(conflict ? "#d00" : "#555")}\" stroke-width=\"{(conflict ? 2.4 : 1.4)}\" {(conflict ? "stroke-dasharray=\"5 3\"" : "")} marker-end=\"url(#{(conflict ? "arrR" : "arr")})\"/>");
        }
        foreach (var n in nodes)
        {
            var (x, y) = pos[n];
            b.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"30\" fill=\"#eef3fb\" stroke=\"#247\"/>");
            b.Append($"<text x=\"{F(x)}\" y=\"{F(y + 4)}\" text-anchor=\"middle\" fill=\"#123\">{System.Net.WebUtility.HtmlEncode(n)}</text>");
        }
        b.Append("</svg>");
        return b.ToString();
    }
}
