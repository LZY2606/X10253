#nullable disable
using System.Net;
using System.Text;
using System.Text.Json;

namespace EvidenceRoom;

/// <summary>Server-rendered review UI: SVG waveforms, status windows, dependency graph, evidence.</summary>
public static class Web
{
    private const string Css = """
        body{font-family:-apple-system,"PingFang SC",sans-serif;margin:0;background:#fafbfc;color:#1f2933;font-size:13px}
        .wrap{max-width:1120px;margin:0 auto;padding:16px}
        h1{font-size:18px;margin:0 0 4px} h2{font-size:15px;margin:22px 0 8px;border-left:4px solid #3a6ea5;padding-left:8px}
        .sub{color:#6b7280;font-size:12px;margin-bottom:12px}
        .card{background:#fff;border:1px solid #d8dee9;border-radius:8px;padding:12px;margin-bottom:12px;overflow-x:auto}
        table{border-collapse:collapse;font-size:12px} td,th{border:1px solid #d8dee9;padding:4px 8px;text-align:left;vertical-align:top}
        th{background:#f0f3f6}
        .badge{display:inline-block;padding:1px 8px;border-radius:10px;color:#fff;font-size:11px}
        .b-ok{background:#2e9e5b}.b-violation{background:#d64545}.b-undetermined{background:#e08a2e}
        .b-unknown{background:#6b7280}.b-stale{background:#8a94a0}.b-current{background:#3a6ea5}
        button{background:#3a6ea5;color:#fff;border:0;border-radius:5px;padding:5px 12px;cursor:pointer;font-size:12px;margin:2px}
        button.warn{background:#d64545} button.ghost{background:#fff;color:#1f2933;border:1px solid #d8dee9}
        textarea{width:100%;height:140px;font-family:ui-monospace,Menlo,monospace;font-size:11px;border:1px solid #d8dee9;border-radius:6px;padding:8px;box-sizing:border-box}
        input[type=text]{border:1px solid #d8dee9;border-radius:4px;padding:3px 6px;font-size:12px}
        select{border:1px solid #d8dee9;border-radius:4px;padding:3px;font-size:12px}
        .id{font-family:ui-monospace,Menlo,monospace;color:#6b7280;font-size:10.5px}
        .err{background:#fdecec;border-color:#d64545}
        .msg{background:#e8f5ec;border:1px solid #2e9e5b;border-radius:6px;padding:8px;margin-bottom:12px;white-space:pre-wrap}
        .banner{background:#eef3fb;border:1px solid #3a6ea5;border-radius:6px;padding:8px;margin-bottom:12px}
        .conflict{background:#fdecec;border:1px solid #d64545;border-radius:6px;padding:8px;margin-bottom:12px}
        .diag{color:#e08a2e}
        svg text{font-size:10px}
        .muted{color:#6b7280}
        a{color:#3a6ea5}
        """;

    public static string Esc(object o) => WebUtility.HtmlEncode(o?.ToString() ?? "");

    public static string Badge(string status) =>
        $"<span class='badge b-{Esc(status)}'>{Esc(status)}</span>";

    public static string Page(Workspace ws, long? asOf = null, string flash = null, string error = null)
    {
        var events = ws.Store.Events(asOf);
        var results = ws.Store.Results(null, asOf);
        var staleAt = new HashSet<string>();
        foreach (var e in events.Where(e => e.Kind == "input-submitted"))
        {
            var p = J.De<JsonElement>(e.PayloadJson);
            if (p.TryGetProperty("staledResultIds", out var s))
                foreach (var x in s.EnumerateArray()) staleAt.Add(x.GetString()!);
        }
        results = results.Select(r => staleAt.Contains(r.Id) ? r with { Status = "stale" } : r).ToList();

        var wave = ws.Store.LatestInput("waveform");
        var thrs = ws.Store.LatestInput("thresholds");
        var deps = ws.Store.LatestInput("dependencies");

        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=zh-CN><head><meta charset=utf-8><title>电源次序证据室</title><style>{Css}</style></head><body><div class=wrap>");
        sb.Append("<h1>电源次序证据室：轨压窗口、依赖约束与启动反例</h1>");
        sb.Append("<div class=sub>本地优先 · SQLite · 原始输入不可覆盖 · 派生值带算法版本/参数指纹/直接来源 · 事件史可回放</div>");
        if (asOf is not null)
        {
            var last = events.LastOrDefault();
            sb.Append($"<div class=banner>🕑 正在查看事件 <b>#{asOf}</b>（{Esc(last?.Kind)}）时刻的依赖版本与可见结论，历史不被最新计算覆盖。 <a href='/'>回到当前</a></div>");
        }
        if (!string.IsNullOrEmpty(error))
            sb.Append($"<div class='card err'><b>明确的格式错误：</b><br>{Esc(error).Replace("\n", "<br>")}</div>");
        if (!string.IsNullOrEmpty(flash))
            sb.Append($"<div class=msg>{Esc(flash)}</div>");

        sb.Append("<h2>① 原始输入：波形 · 阈值 · 滞回 · 最小稳定 · 依赖图 · 采样时钟 · 测量误差</h2>");
        sb.Append(RenderInputs(wave, thrs, deps));
        if (wave is not null && thrs is not null)
            sb.Append(RenderWaveforms(wave, thrs, results));

        sb.Append("<h2>② 状态窗口（上升/下降不同阈值；持续时间判定 glitch 还是违约）</h2>");
        sb.Append(RenderRailResults(results));

        sb.Append("<h2>③ 依赖图（循环依赖给出最小矛盾边集的全部合法候选）</h2>");
        sb.Append(RenderDepGraph(wave, deps, results));

        sb.Append("<h2>④ 第一个违约证据（可追到源输入、计算步骤、人工决定）</h2>");
        sb.Append(RenderFirstViolation(results));

        sb.Append("<h2>⑤ 候选 · 拒绝原因 · 误差 · 未知状态（一等数据）</h2>");
        sb.Append(RenderChecks(results));

        sb.Append("<h2>⑥ 人工裁定 · 冲突 · 撤销（写入事件史）</h2>");
        sb.Append(RenderDecisions(ws, results, asOf));

        sb.Append("<h2>⑦ 版本 · 导入 · 导出再导入（身份/顺序/来源保持）</h2>");
        sb.Append(RenderIO(ws));

        sb.Append("<h2>⑧ 事件史（点序号回到当时）</h2>");
        sb.Append(RenderEvents(ws, asOf));

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string RenderInputs(InputVersion wave, InputVersion thrs, InputVersion deps)
    {
        var sb = new StringBuilder("<div class=card>");
        void VersionRow(string name, InputVersion v, string summary)
        {
            if (v is null) { sb.Append($"<div><b>{Esc(name)}</b>：<span class=muted>尚未导入</span></div>"); return; }
            sb.Append($"<div style='margin:3px 0'><b>{Esc(name)}</b> {Esc(v.Label)} " +
                      $"<span class=id>{Esc(v.Id)}</span>" +
                      (v.ParentId is null ? "" : $" <span class=muted>父版本 <span class=id>{Esc(v.ParentId)}</span></span>") +
                      $"<br><span class=muted>{summary}</span></div>");
        }
        if (wave is not null)
        {
            var w = J.De<WaveformPayload>(wave.PayloadJson);
            VersionRow("波形", wave,
                $"时间基准 {Esc(w.TimeBasis)} · 采样时钟 {Esc(w.ClockInterval.Raw)}（规范化 {w.ClockInterval.Normalized * 1000}ms）" +
                $" · 测量误差 {Esc(w.VoltageError.Raw)} · {w.Rails.Count} 轨：" + string.Join("、", w.Rails.Select(r => r.Name)));
        }
        else VersionRow("波形", null, "");
        if (thrs is not null)
        {
            var t = J.De<ThresholdPayload>(thrs.PayloadJson);
            VersionRow("阈值", thrs, string.Join("；", t.Rails.Select(kv =>
                $"{kv.Key}: rise {kv.Value.Rise.Raw} / fall {kv.Value.Fall.Raw} / 滞回 {(kv.Value.Hysteresis):0.###}V / minStable {kv.Value.MinStable.Raw}")));
        }
        else VersionRow("阈值", null, "");
        VersionRow("依赖", deps, deps is null ? "" :
            string.Join("，", J.De<DependencyPayload>(deps.PayloadJson).Edges.Select(e =>
                $"{e.From}→{e.To}(≥{e.MinDelay.Raw},{e.Phase})")));
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RailSvg(RailWaveform rail, RailThreshold th, Engine.RailAnalysis a,
        double clockInterval)
    {
        var pts = rail.Samples.Where(s => s.T.Normalized is not null && s.V.Normalized is not null)
            .Select(s => (t: s.T.Normalized.Value, v: s.V.Normalized.Value)).ToList();
        if (pts.Count == 0) return "<div class=muted>无可规范化样本，无法画图（原始值仍保留在导入层）</div>";
        const int W = 1040, H = 150, ML = 46, MR = 10, MT = 14, MB = 22;
        var t0 = pts.Min(p => p.t);
        var t1 = pts.Max(p => p.t);
        var vMax = Math.Max(pts.Max(p => p.v), (th.Rise.Normalized ?? 0) * 1.15);
        double X(double t) => ML + (t - t0) / (t1 - t0) * (W - ML - MR);
        double Y(double v) => MT + (1 - v / vMax) * (H - MT - MB);

        var sb = new StringBuilder();
        sb.Append($"<svg width={W} height={H} viewBox='0 0 {W} {H}' style='background:#fff'>");
        foreach (var w in a.Windows)
        {
            var x = X(w.Start); var x2 = X(Math.Min(w.End, t1));
            var fill = w.State switch { "high" => "#c8e6c9", "unknown" => "#fff3b0", _ => "#eef0f2" };
            sb.Append($"<rect x='{x:0}' y='{MT}' width='{Math.Max(1, x2 - x):0}' height='{H - MT - MB}' fill='{fill}' opacity='0.55'/>");
            if (w.State == "unknown")
                sb.Append($"<text x='{(x + x2) / 2:0}' y='{MT + 12}' text-anchor=middle fill='#8a6d00'>时钟缺口·未知</text>");
        }
        // threshold lines
        sb.Append($"<line x1={ML} x2={W - MR} y1='{Y(th.Rise.Normalized.Value):0}' y2='{Y(th.Rise.Normalized.Value):0}' stroke='#d64545' stroke-dasharray='5 3'/>");
        sb.Append($"<text x=4 y='{Y(th.Rise.Normalized.Value) + 3:0}' fill='#d64545'>rise {Esc(th.Rise.Raw)}</text>");
        sb.Append($"<line x1={ML} x2={W - MR} y1='{Y(th.Fall.Normalized.Value):0}' y2='{Y(th.Fall.Normalized.Value):0}' stroke='#2b6cb0' stroke-dasharray='5 3'/>");
        sb.Append($"<text x=4 y='{Y(th.Fall.Normalized.Value) + 3:0}' fill='#2b6cb0'>fall {Esc(th.Fall.Raw)}</text>");
        // waveform polyline
        sb.Append("<polyline fill=none stroke='#1f2933' stroke-width=1.6 points='");
        sb.Append(string.Join(" ", pts.Select(p => $"{X(p.t):0.0},{Y(p.v):0.0}")));
        sb.Append("'/>");
        foreach (var p in pts)
            sb.Append($"<circle cx='{X(p.t):0.0}' cy='{Y(p.v):0.0}' r=1.6 fill='#1f2933'/>");
        // glitches
        foreach (var g in a.Events.Where(e => e.Kind == "glitch"))
            sb.Append($"<polygon points='{X(g.At):0.0},{MT + 6} {X(g.At) - 5:0.0},{MT + 16} {X(g.At) + 5:0.0},{MT + 16}' fill='#e08a2e'><title>{Esc(g.Detail)}</title></polygon>");
        // axis labels
        for (var t = 0; t <= t1 * 1000 + 0.1; t += 2)
            sb.Append($"<text x='{X(t / 1000.0):0.0}' y='{H - 6}' text-anchor=middle fill=#6b7280>{t}ms</text>");
        sb.Append($"<text x={ML - 30} y=12 fill=#6b7280>{Esc(rail.Unit)}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string RenderWaveforms(InputVersion waveIn, InputVersion thrIn, List<DerivedResult> results)
    {
        var wave = J.De<WaveformPayload>(waveIn.PayloadJson);
        var thrs = J.De<ThresholdPayload>(thrIn.PayloadJson);
        var win = results.Where(r => r.Kind == "rail-windows").OrderByDescending(r => r.EventSeq).FirstOrDefault();
        var analyses = new Dictionary<string, Engine.RailAnalysis>();
        if (win is not null)
        {
            var doc = J.De<Dictionary<string, JsonElement>>(win.PayloadJson);
            foreach (var kv in doc)
                analyses[kv.Key] = kv.Value.Deserialize<Engine.RailAnalysis>();
        }
        var sb = new StringBuilder();
        foreach (var rail in wave.Rails)
        {
            sb.Append($"<div class=card><b>{Esc(rail.Name)}</b> <span class=id>轨源 {Esc(waveIn.Id)}:{Esc(rail.Name)}</span>");
            if (!thrs.Rails.TryGetValue(rail.Name, out var th))
            { sb.Append("<div class=diag>该轨无阈值定义</div></div>"); continue; }
            sb.Append($" <span class=muted>滞回 {(th.Hysteresis):0.###}V · 最小稳定 {Esc(th.MinStable.Raw)} · " +
                      $"时钟 {Esc(wave.ClockInterval.Raw)} · 误差 ±{Esc(wave.VoltageError.Raw)}</span>");
            var a = analyses.GetValueOrDefault(rail.Name) ?? new Engine.RailAnalysis(rail.Name, new(), new(),
                new() { "尚无派生结果" }, null, null);
            sb.Append(RailSvg(rail, th, a, wave.ClockInterval.Normalized.Value));
            sb.Append("</div>");
        }
        return sb.ToString();
    }
