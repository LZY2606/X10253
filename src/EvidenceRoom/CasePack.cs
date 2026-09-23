using System.Text.Json;

namespace EvidenceRoom;

/// <summary>Loads and replays the offline case pack shipped in the repo.</summary>
public static class CasePack
{
    public static string? FindDir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "casepack");
                if (File.Exists(Path.Combine(candidate, "01-waveform-baseline.json")))
                    return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    public static List<(string File, string Text)> ImportSteps()
    {
        var dir = FindDir() ?? throw new DirectoryNotFoundException("找不到 casepack 目录");
        return Directory.GetFiles(dir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith("07"))
            .OrderBy(f => f)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
    }

    /// <summary>Replay the full narrative: baseline -> revision -> cycle -> local change -> conflicting decisions.</summary>
    public static List<string> Run(Workspace ws)
    {
        var log = new List<string>();
        foreach (var (file, text) in ImportSteps())
        {
            var outcome = ws.ImportText(text);
            log.Add($"导入 {file}: {outcome.Input.Id}（过期 {outcome.StaledResultIds.Count} 条派生结果）");
        }

        var target = ws.Store.Results("first-violation").LastOrDefault(r => r.Status == "current")
                  ?? ws.Store.Results("first-violation").LastOrDefault();
        if (target is null) { log.Add("无 first-violation 结果，跳过决定演练"); return log; }

        var dir = FindDir()!;
        var script = J.De<JsonElement>(File.ReadAllText(Path.Combine(dir, "07-decisions-script.json")));
        long? lastRejectSeq = null;
        foreach (var step in script.GetProperty("steps").EnumerateArray())
        {
            var action = step.GetProperty("action").GetString()!;
            if (action == "decide")
            {
                long[] resolves = Array.Empty<long>();
                if (step.TryGetProperty("resolves", out var r) && r.GetString() == "open-conflict")
                    resolves = ws.Conflicts().Where(c => c.Target == target.Id)
                        .SelectMany(c => c.Sides).Select(d => d.Seq).ToArray();
                var ev = ws.Decide(target.Id, step.GetProperty("verdict").GetString()!,
                    step.GetProperty("reviewer").GetString()!, step.GetProperty("note").GetString()!, resolves);
                if (step.GetProperty("verdict").GetString() == "reject") lastRejectSeq = ev.Seq;
                log.Add($"决定 {step.GetProperty("verdict").GetString()} by {step.GetProperty("reviewer").GetString()} -> 事件 #{ev.Seq}");
            }
            else if (action == "undo-last-reject" && lastRejectSeq is not null)
            {
                var ev = ws.Undo(lastRejectSeq.Value);
                log.Add($"撤销事件 #{lastRejectSeq} -> 事件 #{ev.Seq}");
            }
        }
        return log;
    }
}
