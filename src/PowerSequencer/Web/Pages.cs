using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PowerSequencer.App;
using PowerSequencer.Domain;
using PowerSequencer.Persistence;

namespace PowerSequencer.Web;

public static class Pages
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string Us(double s) => (s * 1e6).ToString("0.##", CultureInfo.InvariantCulture);
    private static string StateName(RailState s) => s == RailState.High ? "HIGH" : s == RailState.Low ? "LOW" : "UNKNOWN";
    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    public static string Layout(string title, string body, string? banner = null) => $$"""
        <!doctype html><html lang="zh"><head><meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1"/>
        <title>{{H(title)}} · 电源次序证据室</title>
        <style>
        :root{--ink:#0f172a;--muted:#64748b;--line:#e2e8f0;--red:#b4232a;--green:#15803d;--amber:#b45309;--purple:#7c3aed}
        *{box-sizing:border-box}
        body{margin:0;font:14px/1.55 -apple-system,"PingFang SC","Microsoft YaHei",sans-serif;color:var(--ink);background:#f8fafc}
        header{background:#0f172a;color:#fff;padding:10px 22px;display:flex;gap:18px;align-items:center;flex-wrap:wrap}
        header a{color:#cbd5e1;text-decoration:none} header a:hover{color:#fff}
        main{max-width:1040px;margin:0 auto;padding:20px 22px 80px}
        h1{font-size:20px;margin:6px 0 14px} h2{font-size:16px;margin:26px 0 8px;border-left:4px solid #0f4c81;padding:1px}
        .banner{background:#fef3c7;border:1px solid #f59e0b;padding:8px 12px;border-radius:6px;margin:8px 0 16px}
        .card{background:#fff;border:1px solid var(--line);border-radius:8px;padding:14px 16px;margin:10px 0}
        table{border-collapse:collapse;width:100%;font-size:12.5px;background:#fff}
        th,td{border:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}
        th{background:#f1f5f9}
        code,.mono{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:12px}
        .id{color:#475569;font-size:11px}
        .badge{display:inline-block;padding:1px 7px;border-radius:10px;font-size:11px;color:#fff}
        .current{background:var(--green)} .stale{background:#94a3b8} .violated{background:var(--red)}
        .satisfied{background:var(--green)} .ambiguous{background:var(--amber)} .unknown{background:#64748b}
        .adopted{background:#0f4c81} .notadopted{background:#94a3b8} .retracted{background:#6b7280;text-decoration:line-through}
        textarea{width:100%;font-family:ui-monospace,Menlo,monospace;font-size:12px;padding:8px;border:1px solid #cbd5e1;border-radius:6px}
        input[type=text]{font-family:ui-monospace,Menlo,monospace;font-size:12px;padding:5px 8px;border:1px solid #cbd5e1;border-radius:6px}
        button{background:#0f4c81;color:#fff;border:0;border-radius:6px;padding:6px 14px;cursor:pointer;font-size:13px}
        button.ghost{background:#fff;color:#0f4c81;border:1px solid #0f4c81}
        button.danger{background:var(--red)}
        .muted{color:var(--muted)} .err{color:var(--red)}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:14px}
        .evidence{border-left:5px solid var(--red);background:#fef2f2}
        .row{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin:6px 0}
        pre{background:#0f172a;color:#e2e8f0;padding:10px;border-radius:6px;overflow:auto;font-size:11.5px;max-height:340px}
        </style></head><body>
        <header><b>电源次序证据室</b>
        <a href="/">项目总览</a><a href="/review">复核室</a><a href="/events">事件历史</a><a href="/samples">样例/格式错误</a>
        <a href="/export">导出档案</a>
        <span class="muted" style="margin-left:auto">{{DateTime.UtcNow:yyyy-MM-dd}} (local-first, SQLite)</span></header>
        <main>{{banner}}<h1>{{H(title)}}</h1>{{body}}</main></body></html>
        """;

    public static string Banner(long asOf, string eventId) => $"""
        <div class="banner"><b>历史视图 @ <span class="mono">{eventId}</span> (seq {asOf})</b> —
        展示该时点已存在的输入、约束版本与判定结论；最新计算不会覆盖此视图。
        <a href="/review">返回当前视图</a></div>
        """;

    public static string Dashboard(Store store, string project)
    {
        var p = store.Project(project);
        var body = new StringBuilder();

        body.Append("""
        <div class="row"><form method="post" action="/fork"><button class="ghost">分叉阈值版本（在复核室操作）</button></form>
        <a href="/review"><button>进入复核室</button></a></div>
        """);

        body.Append("<h2>轨压 / enable / good 输入（不可变，修订即新版本）</h2>");
        body.Append("<table><tr><th>身份</th><th>信号</th><th>类型</th><th>版本链</th><th>采样时钟</th><th>误差</th><th>内容哈希</th></tr>");
        foreach (var i in p.Inputs.OrderBy(i => i.StreamId).ThenBy(i => i.Version))
        {
            var wf = i.Data.Deserialize<WaveformData>(Store.JsonOptions)!;
            body.Append($"<tr><td class=\"mono\">{H(i.StreamId)}<br><a href=\"/inputs/{H(i.StreamId)}\">原始表示</a></td>");
            body.Append($"<td>{H(wf.Rail)}</td><td>{H(wf.SignalKind)}</td><td>v{i.Version}" +
                        (i.ParentVersion is null ? "" : $" ← v{i.ParentVersion}") + "</td>");
            body.Append($"<td class=\"mono\">{H(wf.SampleClock.ToString())} ± {H(wf.ClockJitter.ToString())}</td>");
            body.Append($"<td class=\"mono\">{H(wf.MeasurementError.ToString())}</td>");
            body.Append($"<td class=\"id\">{H(i.Hash[..14])}…</td></tr>");
        }
        body.Append("</table>");

        body.Append("<div class=\"card\"><b>导入新波形</b>（原始单位与坐标基准原样保留；无法转换的值留在原位置并附诊断）" +
            "<form method=\"post\" action=\"/import/waveform\"><textarea name=\"json\" rows=\"9\" placeholder=\"见 /samples 页可复制的小样例\"></textarea>" +
            "<br><button>提交原始输入</button></form></div>");

        var core = p.Inputs.Where(i => i.Data.Deserialize<WaveformData>(Store.JsonOptions)!.SignalKind == "voltage")
            .GroupBy(i => i.StreamId).Select(g => g.OrderBy(i => i.Version).Last()).FirstOrDefault();
        if (core is not null)
        {
            body.Append($"<div class=\"card\"><b>修订已有输入（新版本 + 父版本关系）</b>" +
                $"<form method=\"post\" action=\"/import/revise\">" +
                $"<input type=\"hidden\" name=\"streamId\" value=\"{H(core.StreamId)}\"/>" +
                $"<p class=\"muted\">流 <span class=\"mono\">{H(core.StreamId)}</span> 的新版本 JSON：</p>" +
                "<textarea name=\"json\" rows=\"5\"></textarea><br><button>提交修订版本</button></form></div>");
        }

        body.Append("<h2>阈值/时序约束版本（父版本关系 + 参数指纹）</h2>");
        body.Append("<table><tr><th>身份</th><th>版本链</th><th>轨压数</th><th>依赖边</th><th>参数指纹</th><th>操作</th></tr>");
        foreach (var c in p.Constraints.OrderBy(c => c.Id).ThenBy(c => c.Version))
        {
            var cv = c.Data.Deserialize<ConstraintSet>(Store.JsonOptions)!;
            body.Append($"<tr><td class=\"mono\">{H(c.Id)}</td><td>v{c.Version}" +
                        (c.ParentVersion is null ? "" : $" ← v{c.ParentVersion}") + "</td>");
            body.Append($"<td>{cv.Rails.Count}</td><td>{cv.Edges.Count}</td>");
            body.Append($"<td class=\"id\">{H(c.Fingerprint[..14])}…</td>");
            body.Append($"<td><a href=\"/review?asOf={c.Seq}\">在复核室查看</a></td></tr>");
        }
        body.Append("</table>");

        body.Append("<h2>派生结果（算法版本 + 参数指纹 + 直接来源；过期结果可查看但不冒充当前）</h2>");
        body.Append("<table><tr><th>稳定身份</th><th>类型</th><th>对象</th><th>算法</th><th>状态</th><th>直接来源</th></tr>");
        foreach (var r in p.Results.OrderBy(r => r.Seq))
        {
            body.Append($"<tr><td class=\"mono\"><a href=\"/trace/{H(r.Id)}\">{H(r.Id)}</a></td>");
            body.Append($"<td>{H(r.Kind)}</td><td>{H(r.Rail ?? "-")}</td><td class=\"mono\">{H(r.AlgoVersion)}</td>");
            body.Append($"<td><span class=\"badge {r.Status}\">{r.Status}</span></td>");
            body.Append($"<td class=\"id\">{string.Join("<br>", r.Sources.Select(s => H(s.ToString())))}</td></tr>");
        }
        body.Append("</table>");

        body.Append("<h2>人工裁定（冲突双方都保留；被采纳/未采纳均标记）</h2>");
        body.Append(DecisionTable(p, null));

        body.Append("<h2>档案导入 / 重置</h2>");
        body.Append("""
        <div class="card">导入导出的档案（JSON）——导出、删除本地数据库再导入后，身份、顺序与来源关系保持：
        <form method="post" action="/import/archive"><textarea name="archive" rows="6" placeholder="粘贴 powerseq-archive/1 JSON"></textarea>
        <br><button>导入档案（要求该项目为空）</button></form></div>
        <form method="post" action="/project/reset" onsubmit="return confirm('清空本地数据库中的本项目全部事件？')">
        <button class="danger">清空本地数据库（本项目）</button> <span class="muted">清空后可用上面的档案导入或重启自动恢复内置案例包</span></form>
        """);
        return Layout("项目总览", body.ToString());
    }

    public static string DecisionTable(Projections p, string? target)
    {
        var ds = p.Decisions.Where(d => target is null || d.Target == target)
            .OrderBy(d => d.Seq).ToList();
        var sb = new StringBuilder();
        if (ds.Count == 0) sb.Append("<p class=\"muted\">尚无人工裁定。</p>");
        sb.Append("<table><tr><th>决定身份</th><th>对象结果</th><th>动作</th><th>理由</th><th>状态</th><th>冲突</th><th></th></tr>");
        foreach (var d in ds)
        {
            var status = d.Retracted ? "<span class=\"badge retracted\">retracted</span>"
                : d.Adopted ? "<span class=\"badge adopted\">adopted</span>"
                : "<span class=\"badge notadopted\">not adopted</span>";
            sb.Append($"<tr><td class=\"mono\">{H(d.Id)}</td><td class=\"mono\">{H(d.Target)}</td>");
            sb.Append($"<td>{H(d.Action)}</td><td>{H(d.Rationale)}</td><td>{status}</td>");
            sb.Append($"<td class=\"id\">{string.Join("<br>", d.ConflictsWith.Select(H))}</td>");
            sb.Append($"<td>{(d.Retracted ? "" : $"<form method=\"post\" action=\"/decision/retract\"><input type=\"hidden\" name=\"id\" value=\"{H(d.Id)}\"/><button class=\"ghost\">撤销</button></form>")}</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    public static string Review(Store store, string project, int? asOf)
    {
        var full = store.Project(project);
        long asOfSeq = asOf ?? store.LastSeq(project);
        var p = store.Project(project, asOfSeq);
        var csV = p.LatestConstraints();
        var body = new StringBuilder();
        if (asOf is not null)
        {
            var eid = p.EventLog.LastOrDefault()?.Id ?? "ev-000000";
            body.Append(Banner(asOfSeq, eid));
        }

        if (csV is null)
        {
            body.Append("<p>尚未导入约束。先在项目总览导入波形与约束（内置案例包已自带）。</p>");
            return Layout("复核室", body.ToString());
        }
        var cs = csV.Data.Deserialize<ConstraintSet>(Store.JsonOptions)!;

        // version switcher + as-of selector
        body.Append("<div class=\"card\"><div class=\"row\"><b>约束版本：</b>");
        foreach (var c in p.Constraints.GroupBy(c => c.Id).Select(g => g.OrderBy(c => c.Version).Last()).OrderBy(c => c.Id))
            body.Append($"<a class=\"badge current\" href=\"/review?asOf={c.Seq}\">{H(c.Id)} v{c.Version}</a> ");
        body.Append("</div><div class=\"row muted\">跳回事件： ");
        foreach (var e in full.EventLog.TakeLast(14))
            body.Append($"<a href=\"/review?asOf={e.Seq}\" class=\"mono\">{e.Id}</a> ");
        body.Append("</div></div>");

        // first violation
        var first = p.Results.FirstOrDefault(r => r.Kind == "first-violation");
        if (first is not null)
        {
            var fv = first.Payload.Deserialize<FirstViolationResult>(Store.JsonOptions)!;
            body.Append("<h2>第一个违约证据</h2><div class=\"card evidence\">");
            if (fv.Violation is null)
                body.Append("<b>未发现违约。</b>");
            else
            {
                var v = fv.Violation;
                body.Append($"<p><b>{H(v.Kind)}</b> @ <span class=\"mono\">{Us(v.At)} us</span> — {H(v.Description)}</p>");
                body.Append($"<p>证据结果：<a class=\"mono\" href=\"/trace/{H(v.EvidenceResultId)}\">{H(v.EvidenceResultId)}</a></p>");
                body.Append($"<p class=\"id\">来源：{string.Join(", ", v.Sources.Select(s => H(s.ToString())))}</p>");
            }
            body.Append($"<div class=\"muted\">本结论身份 <span class=\"mono\">{H(first.Id)}</span> · " +
                        $"<span class=\"badge {first.Status}\">{first.Status}</span> · 候选 {fv.Considered.Count} 个：" +
                        (fv.Considered.Count == 0 ? "（无）" : string.Join("；", fv.Considered.Take(6).Select(H))) + "</div></div>");
        }

        // waveforms
        body.Append("<h2>轨压波形与状态窗口（上升/下降阈值不同；持续时间决定 glitch/违约）</h2>");
        var firstViolationT = GetViolationT(full, asOf, first);
        foreach (var rc in cs.Rails)
        {
            var rr = p.Results.FirstOrDefault(r => r.Kind == "rail-analysis" && r.Rail == rc.Rail);
            var input = p.Inputs.OrderByDescending(i => i.Version)
                .FirstOrDefault(i => string.Equals(i.Data.Deserialize<WaveformData>(Store.JsonOptions)!.Rail, rc.Rail, StringComparison.OrdinalIgnoreCase));
            if (rr is null || input is null) continue;
            var rail = rr.Payload.Deserialize<RailAnalysisResult>(Store.JsonOptions)!;
            var wf = input.Data.Deserialize<WaveformData>(Store.JsonOptions)!;
            body.Append("<div class=\"card\">");
            body.Append($"<div class=\"row\"><b>{H(rc.Rail)}</b> " +
                        $"<span class=\"badge {rr.Status}\">{rr.Status}</span> " +
                        $"<span class=\"id mono\">{H(rr.Id)}</span></div>");
            double? vT = null;
            if (firstViolationT is { } fv && fv.rail == rc.Rail) vT = fv.t;
            body.Append(Svg.Waveform(wf, rc, rail, rr.Id, vT));
            body.Append("<table><tr><th>窗口</th><th>状态</th><th>开始(us)</th><th>结束(us)</th><th>确定</th><th>依据</th></tr>");
            foreach (var w in rail.Windows)
                body.Append($"<tr><td class=\"mono\">win:{H(w.Rail)}:{Us(w.TStart)}</td><td>{StateName(w.State)}</td>" +
                            $"<td>{Us(w.TStart)}</td><td>{Us(w.TEnd)}</td><td>{(w.Certain ? "是" : "<b class='err'>未知/误差带</b>")}</td><td class=\"id\">{H(w.Reason)}</td></tr>");
            body.Append("</table>");
            if (rail.Excursions.Count > 0)
            {
                body.Append("<table><tr><th>越界事件</th><th>方向</th><th>时长(us)</th><th>判定</th><th>候选</th><th>依据</th></tr>");
                foreach (var ex in rail.Excursions)
                {
                    var color = ex.Classification switch
                    {
                        ExcursionClass.Glitch => "ambiguous",
                        ExcursionClass.Violation => "violated",
                        ExcursionClass.Ambiguous => "unknown",
                        _ => "unknown"
                    };
                    body.Append($"<tr><td class=\"mono\">ex:{H(ex.Rail)}:{Us(ex.TStart)}</td><td>{H(ex.Direction)}</td>" +
                                $"<td>{Us(ex.Duration)}</td><td><span class=\"badge {color}\">{H(ex.Classification)}</span></td>" +
                                $"<td>{string.Join(",", ex.Candidates.Select(H))}</td><td class=\"id\">{H(ex.Reason)}</td></tr>");
                }
                body.Append("</table>");
            }
            if (rail.Diagnostics.Count > 0)
            {
                body.Append("<table><tr><th>诊断（无法转换的值留在原位置）</th></tr>");
                foreach (var d in rail.Diagnostics)
                    body.Append($"<tr><td><b class=\"mono\">{H(d.Code)}</b> {H(d.Message)}</td></tr>");
                body.Append("</table>");
            }
            body.Append(DecisionForm(rr.Id));
            body.Append("</div>");
        }

        // sequence
        var seqR = p.Results.FirstOrDefault(r => r.Kind == "sequence-analysis");
        if (seqR is not null)
        {
            var seq = seqR.Payload.Deserialize<SequenceAnalysisResult>(Store.JsonOptions)!;
            body.Append("<h2>启动 / 关机序列（关机必须逆序；同时到达保留全部合法候选）</h2><div class=\"card\">");
            body.Append($"<div class=\"row\"><span class=\"badge {seqR.Status}\">{seqR.Status}</span> <span class=\"id mono\">{H(seqR.Id)}</span></div>");
            body.Append("<div class=\"grid2\"><div><b>启动顺序</b><ol>");
            foreach (var s in seq.Startup)
                body.Append($"<li>{string.Join(" ≈ ", s.Rails.Select(H))} @ {Us(s.Time)}us <span class=\"id\">{H(s.Basis)}</span></li>");
            body.Append("</ol>");
            body.Append($"<p class=\"muted\">全部合法排序候选（{seq.StartupCandidates.Count}）：</p><ul>");
            foreach (var cand in seq.StartupCandidates)
                body.Append($"<li class=\"mono\">{string.Join(" → ", cand.Select(H))}</li>");
            body.Append("</ul></div><div><b>关机顺序（实际）</b><ol>");
            foreach (var s in seq.Shutdown)
                body.Append($"<li>{string.Join(" ≈ ", s.Rails.Select(H))} @ {Us(s.Time)}us</li>");
            body.Append("</ol>");
            if (seq.Mismatches.Count == 0) body.Append("<p class=\"badge satisfied\">关机逆序满足</p>");
            else
                foreach (var m in seq.Mismatches)
                    body.Append($"<p><span class=\"badge violated\">关机逆序违约</span> 位置 {m.Index}：期望 {string.Join(" → ", m.Expected.Select(H))}，实际 {string.Join(" → ", m.Actual.Select(H))}<br><span class=\"id\">{H(m.Reason)}</span></p>");
            body.Append("</div></div>");
            foreach (var d in seq.Diagnostics)
                body.Append($"<p class=\"id\"><b>{H(d.Code)}</b>: {H(d.Message)}</p>");
            body.Append(DecisionForm(seqR.Id)).Append("</div>");
        }

        // graph
        var grR = p.Results.FirstOrDefault(r => r.Kind == "graph-analysis");
        if (grR is not null)
        {
            var gr = grR.Payload.Deserialize<GraphAnalysisResult>(Store.JsonOptions)!;
            body.Append("<h2>依赖图与最小矛盾边集（循环依赖）</h2><div class=\"card\">");
            body.Append($"<div class=\"row\"><span class=\"badge {grR.Status}\">{grR.Status}</span> <span class=\"id mono\">{H(grR.Id)}</span></div>");
            body.Append(Svg.Graph(cs, gr));
            body.Append("<table><tr><th>边</th><th>类型</th><th>判定</th><th>依据</th></tr>");
            foreach (var v in gr.Verdicts)
                body.Append($"<tr><td class=\"mono\">{H(v.Edge.From)} → {H(v.Edge.To)}</td><td>{H(v.Edge.Kind)}</td>" +
                            $"<td><span class=\"badge {v.Verdict}\">{v.Verdict}</span></td><td class=\"id\">{H(v.Reason)}</td></tr>");
            body.Append("</table>");
            if (gr.Cycles is null) body.Append("<p class=\"badge satisfied\">依赖图无环</p>");
            else
            {
                body.Append($"<p><b>最小矛盾边集（{gr.Cycles.MinimalEdgeSets.Count} 个等优候选）：</b><span class=\"id\">{H(gr.Cycles.Reason)}</span></p><ol>");
                foreach (var set in gr.Cycles.MinimalEdgeSets)
                    body.Append($"<li>{string.Join("; ", set.Select(e => $"<span class='mono'>{H(e.From)}→{H(e.To)}</span>"))}</li>");
                body.Append("</ol>");
            }
            foreach (var d in gr.Diagnostics)
                body.Append($"<p class=\"id\"><b>{H(d.Code)}</b>: {H(d.Message)}</p>");
            body.Append(DecisionForm(grR.Id)).Append("</div>");
        }

        // decisions on this view
        body.Append("<h2>本视图的人工裁定与冲突</h2><div class=\"card\">").Append(DecisionTable(p, null)).Append("</div>");

        // fork
        body.Append("<h2>分叉阈值版本</h2><div class=\"card\">");
        body.Append($"<form method=\"post\" action=\"/fork\">");
        body.Append($"<input type=\"hidden\" name=\"parentId\" value=\"{H(csV.Id)}\"/>");
        body.Append($"<input type=\"hidden\" name=\"parentVersion\" value=\"{csV.Version}\"/>");
        body.Append("<p>编辑下面的约束 JSON（只改动直接来源；只影响依赖它的结果）：</p>");
        body.Append($"<textarea name=\"json\" rows=\"14\">{H(csV.Data.ToJsonString(Pretty))}</textarea><br>");
        body.Append("<button>基于 v").Append($"{csV.Version} 分叉新版本</button></form></div>");

        return Layout("复核室", body.ToString());
    }

    private static (string rail, double t)? GetViolationT(Projections full, int? asOf, StoredResult? first)
    {
        if (first is null) return null;
        var fv = first.Payload.Deserialize<FirstViolationResult>(Store.JsonOptions)!;
        if (fv.Violation is null) return null;
        var ev = full.Result(fv.Violation.EvidenceResultId);
        if (ev?.Kind != "rail-analysis") return null;
        var rail = ev.Payload.Deserialize<RailAnalysisResult>(Store.JsonOptions)!;
        var ex = rail.Excursions.FirstOrDefault(e => e.Classification == ExcursionClass.Violation && e.TStart == fv.Violation.At);
        return ex is null ? null : (ex.Rail, ex.TStart);
    }

    private static string DecisionForm(string resultId) => $"""
        <form method="post" action="/decide" style="margin-top:8px">
        <input type="hidden" name="target" value="{H(resultId)}"/>
        <input type="hidden" name="returnUrl" value="/review"/>
        <select name="action"><option value="accept">接受</option><option value="reject">否定</option><option value="adjust">参数调整</option></select>
        <input type="text" name="rationale" size="60" placeholder="裁定理由（写入历史事件）" required/>
        <button class="ghost">裁定</button></form>
        """;

    public static string Trace(Store store, string project, string resultId)
    {
        var p = store.Project(project);
        var r = p.Result(resultId);
        var body = new StringBuilder();
        if (r is null) return Layout("溯源", $"<p class=\"err\">找不到结果 {H(resultId)}</p>");
        body.Append($"<div class=\"card\"><b class=\"mono\">{H(r.Id)}</b> · {H(r.Kind)} · 对象 {H(r.Rail ?? "-")} · <span class=\"badge {r.Status}\">{r.Status}</span><p class=\"muted\">算法版本 <span class=\"mono\">{H(r.AlgoVersion)}</span> · 参数指纹 <span class=\"mono\">{H(r.Fingerprint)}</span></p></div>");

        body.Append("<h2>直接来源（可继续下钻到原始输入）</h2><table><tr><th>来源</th><th>身份</th><th>事件</th></tr>");
        foreach (var s in r.Sources)
        {
            var ev = p.EventLog.FirstOrDefault(e =>
                (e.Type == "input.imported" || e.Type == "input.revised") && (string)e.Payload["streamId"]! == s.Id ||
                e.Type == "constraints.created" && (string)e.Payload["id"]! == s.Id.Split(':')[0]);
            body.Append($"<tr><td>{H(s.Kind)}</td><td class=\"mono\">{H(s.ToString())}</td><td class=\"mono\">{(ev is null ? "-" : $"<a href=\"/review?asOf={ev.Seq}\">{ev.Id}</a>")}</td></tr>");
        }
        body.Append("</table>");

        body.Append("<h2>计算值</h2><pre>").Append(H(r.Payload.ToJsonString(Pretty))).Append("</pre>");

        body.Append("<h2>相关人工决定</h2><div class=\"card\">").Append(DecisionTable(p, r.Id)).Append("</div>");
        body.Append(DecisionForm(r.Id));

        body.Append("<h2>失效/再生效事件</h2><table><tr><th>事件</th><th>类型</th><th>理由</th></tr>");
        foreach (var e in p.EventLog.Where(e => e.Type is "result.invalidated" or "result.revalidated" &&
            e.Payload["ids"]!.AsArray().Any(v => (string)v! == r.Id)))
            body.Append($"<tr><td class=\"mono\"><a href=\"/review?asOf={e.Seq}\">{e.Id}</a></td><td>{e.Type}</td><td>{H((string?)e.Payload["reason"])}</td></tr>");
        body.Append("</table>");
        return Layout("溯源 · " + r.Id, body.ToString());
    }

    public static string Events(Store store, string project)
    {
        var events = store.Events(project);
        var body = new StringBuilder("<table><tr><th>序号</th><th>稳定身份</th><th>类型</th><th>时间</th><th>摘要</th><th>回到当时</th></tr>");
        foreach (var e in events)
        {
            var summary = e.Type switch
            {
                "input.imported" => $"import {e.Payload["streamId"]} ({e.Payload["kind"]})",
                "input.revised" => $"revise {e.Payload["streamId"]} -> v{(int)e.Payload["parentVersion"]! + 1}",
                "constraints.created" => $"constraints {e.Payload["id"]} v{(e.Payload["parentVersion"] is null ? 1 : (int)e.Payload["parentVersion"]! + 1)}",
                "analysis.computed" => $"computed {e.Payload["results"]!.AsArray().Count} result(s)",
                "result.invalidated" => $"invalidate {e.Payload["ids"]!.AsArray().Count} result(s): {e.Payload["reason"]}",
                "result.revalidated" => $"revalidate {e.Payload["ids"]!.AsArray().Count} result(s)",
                "decision.made" => $"{e.Payload["action"]} on {e.Payload["target"]} ({e.Payload["rationale"]})",
                "decision.retracted" => $"retract {e.Payload["id"]}",
                "archive.imported" => $"archive imported ({e.Payload["count"]} events)",
                _ => e.Type
            };
            body.Append($"<tr><td>{e.Seq}</td><td class=\"mono\">{e.Id}</td><td>{e.Type}</td><td class=\"id\">{e.CreatedUtc}</td>" +
                        $"<td class=\"id\">{H(summary)}</td><td><a href=\"/review?asOf={e.Seq}\">查看</a></td></tr>");
        }
        body.Append("</table>");
        return Layout("事件历史", body.ToString());
    }

    public static string InputDetail(Store store, string project, string streamId)
    {
        var p = store.Project(project);
        var body = new StringBuilder($"<h2>流 {H(streamId)} 的全部版本</h2>");
        foreach (var i in p.Inputs.Where(i => i.StreamId == streamId).OrderBy(i => i.Version))
        {
            body.Append($"<div class=\"card\"><b>v{i.Version}</b>" +
                        (i.ParentVersion is null ? "（初始导入）" : $"（父版本 v{i.ParentVersion}）") +
                        $" <span class=\"id mono\">{H(i.Hash[..20])}…</span> <span class=\"id\">{H(i.Hash)}</span>" +
                        "<pre>").Append(H(i.Data.ToJsonString(Pretty))).Append("</pre></div>");
        }
        return Layout("原始输入 · " + streamId, body.ToString());
    }

    public static string SamplesPage(string valid, string malformed) => Layout("可复制样例与格式错误", $"""
        <h2>可复制的小型样例（原始单位保留）</h2>
        <div class="card">复制下面 JSON 到 <a href="/">项目总览</a> 的“导入新波形”：
        <pre>{H(valid)}</pre></div>
        <h2>明确的格式错误样例</h2>
        <div class="card">缺少必填字段 / 未知单位（未知单位不会被丢弃，而是留在原位置并附诊断；真正的 JSON/字段错误会被导入层拒绝并列出位置）：
        <pre>{H(malformed)}</pre></div>
        """);

    public static string ImportError(string message, string? received = null) => Layout("导入被拒绝", $"""
        <div class="card"><b class="err">格式错误：</b><pre>{H(message)}</pre>
        {(received is null ? "" : $"<details><summary>已提交内容</summary><pre>{H(received)}</pre></details>")}
        <a href="/">返回</a></div>
        """);
}
