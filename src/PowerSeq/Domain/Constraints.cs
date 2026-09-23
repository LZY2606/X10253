using System.Security.Cryptography;
using System.Text;

namespace PowerSeq.Domain;

public record RailThreshold(string Rail, double RiseV, double FallV, double HystV, double MinStableMs);

public record DependencyEdge(string Before, string After)
{
    public override string ToString() => $"{Before}->{After}";
}

/// <summary>
/// Versioned timing constraint set: per-rail rise/fall thresholds with hysteresis and
/// minimum stable time, plus the rail dependency graph and shutdown policy.
/// </summary>
public record ConstraintSet(
    IReadOnlyDictionary<string, RailThreshold> Thresholds,
    IReadOnlyList<DependencyEdge> Edges,
    bool ShutdownReverse,
    string Fingerprint,
    string RawText);

public static class ConstraintParser
{
    public const string Kind = "constraints";

    /// <summary>
    /// Copyable format:
    ///   [thresholds]
    ///   rail, rise_V, fall_V, hyst_V, min_stable_ms
    ///   VDD_CORE, 0.90, 0.85, 0.02, 0.50
    ///   [dependencies]
    ///   VDD_CORE, VDD_IO        # VDD_CORE 必须先于 VDD_IO 达到 good
    ///   [policy]
    ///   shutdown_reverse, true
    /// </summary>
    public static ConstraintSet Parse(string raw)
    {
        var thresholds = new Dictionary<string, RailThreshold>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<DependencyEdge>();
        var shutdownReverse = true;
        var section = "";
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i];
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new ImportException($"第 {lineNo} 行: 节头格式错误 '{line}'");
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section is not ("thresholds" or "dependencies" or "policy"))
                    throw new ImportException($"第 {lineNo} 行: 未知节 '[{section}]'");
                continue;
            }
            var cols = line.Split(',').Select(c => c.Trim()).ToArray();
            switch (section)
            {
                case "thresholds":
                {
                    if (cols[0].Equals("rail", StringComparison.OrdinalIgnoreCase)) break; // header
                    if (cols.Length != 5) throw new ImportException($"第 {lineNo} 行: 阈值行需要 5 列 rail,rise_V,fall_V,hyst_V,min_stable_ms");
                    var rail = cols[0];
                    var rise = Num(cols[1], lineNo, "rise_V");
                    var fall = Num(cols[2], lineNo, "fall_V");
                    var hyst = Num(cols[3], lineNo, "hyst_V");
                    var minStable = Num(cols[4], lineNo, "min_stable_ms");
                    if (fall > rise) throw new ImportException($"第 {lineNo} 行: fall_V ({fall}) 不应高于 rise_V ({rise})");
                    if (hyst < 0 || minStable < 0) throw new ImportException($"第 {lineNo} 行: 滞回与最小稳定时间不能为负");
                    thresholds[rail] = new RailThreshold(rail, rise, fall, hyst, minStable);
                    break;
                }
                case "dependencies":
                {
                    if (cols[0].Equals("before", StringComparison.OrdinalIgnoreCase)) break;
                    if (cols.Length != 2) throw new ImportException($"第 {lineNo} 行: 依赖行需要两列 before,after");
                    edges.Add(new DependencyEdge(cols[0], cols[1]));
                    break;
                }
                case "policy":
                {
                    if (cols.Length != 2) throw new ImportException($"第 {lineNo} 行: 策略行需要 key,value");
                    if (cols[0].Equals("shutdown_reverse", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!bool.TryParse(cols[1], out shutdownReverse))
                            throw new ImportException($"第 {lineNo} 行: shutdown_reverse 需要 true/false");
                    }
                    else throw new ImportException($"第 {lineNo} 行: 未知策略 '{cols[0]}'");
                    break;
                }
                default:
                    throw new ImportException($"第 {lineNo} 行: 内容必须位于 [thresholds]/[dependencies]/[policy] 节内");
            }
        }
        if (thresholds.Count == 0) throw new ImportException("缺少 [thresholds] 内容");
        return new ConstraintSet(thresholds, edges, shutdownReverse, Fingerprint(raw), raw);
    }

    private static double Num(string s, int lineNo, string col)
    {
        if (!double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v))
            throw new ImportException($"第 {lineNo} 行: 列 {col} 的值 '{s}' 不是数字");
        return v;
    }

    public static string Fingerprint(string raw)
    {
        var hash = SHA256.HashData(Encoding.UTF8.Get(raw.Replace("\r\n", "\n").Trim()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Rails whose thresholds or incident edges changed between two versions.</summary>
    public static List<string> ChangedRails(ConstraintSet oldSet, ConstraintSet newSet)
    {
        var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in newSet.Thresholds)
        {
            if (!oldSet.Thresholds.TryGetValue(kv.Key, out var old) || !old.Equals(kv.Value))
                changed.Add(kv.Key);
        }
        foreach (var kv in oldSet.Thresholds)
            if (!newSet.Thresholds.ContainsKey(kv.Key)) changed.Add(kv.Key);
        var oldEdges = oldSet.Edges.ToHashSet();
        var newEdges = newSet.Edges.ToHashSet();
        foreach (var e in newEdges.Except(oldEdges)) { changed.Add(e.Before); changed.Add(e.After); }
        foreach (var e in oldEdges.Except(newEdges)) { changed.Add(e.Before); changed.Add(e.After); }
        if (oldSet.ShutdownReverse != newSet.ShutdownReverse)
            foreach (var r in newSet.Thresholds.Keys) changed.Add(r);
        return changed.ToList();
    }
}
