using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PowerSequencer;

public sealed record RawInput(string Id, string Chain, int Version, string? ParentId, string Label, string Content, long Seq);
public sealed record DerivedRecord(string Id, string Kind, string Algo, string Fingerprint, string[] Sources, string Content, long Seq);
public sealed record LogEvent(long Seq, string Type, string Payload, string At);

/// <summary>
/// SQLite-backed local-first store. Raw inputs are immutable and versioned by
/// parent links; derived records are content-addressed (algorithm version +
/// parameter fingerprint + direct sources); every action is an appended event.
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IClock _clock;

    public Store(string dbPath, IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS raw_inputs(
                id TEXT PRIMARY KEY,
                chain TEXT NOT NULL,
                version INTEGER NOT NULL,
                parent_id TEXT,
                label TEXT NOT NULL,
                content TEXT NOT NULL,
                seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS derived(
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                algo TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                sources TEXT NOT NULL,
                content TEXT NOT NULL,
                seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY,
                type TEXT NOT NULL,
                payload TEXT NOT NULL,
                at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    public long NextSeq()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta(key,value) VALUES('seq','0')
            ON CONFLICT(key) DO NOTHING;
            UPDATE meta SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key='seq';
            SELECT value FROM meta WHERE key='seq';
            """;
        return long.Parse((string)cmd.ExecuteScalar()!);
    }

    public void BumpSeqTo(long atLeast)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta(key,value) VALUES('seq','0') ON CONFLICT(key) DO NOTHING;
            UPDATE meta SET value = CAST(MAX(CAST(value AS INTEGER), $v) AS TEXT) WHERE key='seq';
            """;
        cmd.Parameters.AddWithValue("$v", atLeast);
        cmd.ExecuteNonQuery();
    }

    public string AddRawInput(string chain, string? parentId, string label, string content, long? forcedSeq = null)
    {
        var id = Identity.Hash(string.Join('\n', chain, parentId ?? "-", label, Identity.Canonical(JsonNode.Parse(content))));
        var existing = GetRaw(id);
        if (existing is not null) return existing.Id;
        var version = Chain(chain).Count + 1;
        var seq = forcedSeq ?? NextSeq();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO raw_inputs(id,chain,version,parent_id,label,content,seq) VALUES($id,$c,$v,$p,$l,$ct,$s)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$c", chain);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$ct", content);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
        return id;
    }

    public RawInput? GetRaw(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,chain,version,parent_id,label,content,seq FROM raw_inputs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RawInput(r.GetString(0), r.GetString(1), r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6));
    }

    public List<RawInput> Chain(string chain, long? atSeq = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,chain,version,parent_id,label,content,seq FROM raw_inputs WHERE chain=$c"
            + (atSeq is null ? "" : " AND seq<=$s") + " ORDER BY seq";
        cmd.Parameters.AddWithValue("$c", chain);
        if (atSeq is not null) cmd.Parameters.AddWithValue("$s", atSeq.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<RawInput>();
        while (r.Read())
            list.Add(new RawInput(r.GetString(0), r.GetString(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6)));
        return list;
    }

    public RawInput? Head(string chain, long? atSeq = null) => Chain(chain, atSeq).LastOrDefault();

    public DerivedRecord GetOrAddDerived(string kind, string algo, string fingerprint,
        string[] sources, Func<string> contentFactory, long? forcedSeq = null)
    {
        var id = Identity.Hash(string.Join('\n', new[] { kind, algo, fingerprint }
            .Concat(sources.OrderBy(s => s, StringComparer.Ordinal))));
        var existing = GetDerived(id);
        if (existing is not null) return existing;
        var content = contentFactory();
        var seq = forcedSeq ?? NextSeq();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO derived(id,kind,algo,fingerprint,sources,content,seq) VALUES($id,$k,$a,$f,$s,$c,$q)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$a", algo);
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$s", JsonHelper.Stringify(sources));
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$q", seq);
        cmd.ExecuteNonQuery();
        return GetDerived(id)!;
    }

    public DerivedRecord? GetDerived(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,kind,algo,fingerprint,sources,content,seq FROM derived WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadDerived(r);
    }

    public List<DerivedRecord> Derived(string? kind = null, long? atSeq = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,kind,algo,fingerprint,sources,content,seq FROM derived WHERE 1=1"
            + (kind is null ? "" : " AND kind=$k") + (atSeq is null ? "" : " AND seq<=$s") + " ORDER BY seq";
        if (kind is not null) cmd.Parameters.AddWithValue("$k", kind);
        if (atSeq is not null) cmd.Parameters.AddWithValue("$s", atSeq.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<DerivedRecord>();
        while (r.Read()) list.Add(ReadDerived(r));
        return list;
    }

    private static DerivedRecord ReadDerived(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(4))!,
            r.GetString(5), r.GetInt64(6));

    public LogEvent AppendEvent(string type, JsonObject payload, long? forcedSeq = null, string? forcedAt = null)
    {
        var seq = forcedSeq ?? NextSeq();
        var at = forcedAt ?? _clock.Now().ToString("O");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO events(seq,type,payload,at) VALUES($s,$t,$p,$a)";
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$p", payload.ToJsonString());
        cmd.Parameters.AddWithValue("$a", at);
        cmd.ExecuteNonQuery();
        return new LogEvent(seq, type, payload.ToJsonString(), at);
    }

    public List<LogEvent> Events(long? atSeq = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seq,type,payload,at FROM events"
            + (atSeq is null ? "" : " WHERE seq<=$s") + " ORDER BY seq";
        if (atSeq is not null) cmd.Parameters.AddWithValue("$s", atSeq.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<LogEvent>();
        while (r.Read()) list.Add(new LogEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public string ExportArchive()
    {
        var root = new JsonObject { ["format"] = "power-sequencer-archive-v1" };
        var raws = new JsonArray();
        foreach (var chain in new[] { "waveform", "thresholds", "dependencies" })
            foreach (var ri in Chain(chain))
                raws.Add(new JsonObject
                {
                    ["id"] = ri.Id, ["chain"] = ri.Chain, ["version"] = ri.Version,
                    ["parent"] = ri.ParentId, ["label"] = ri.Label,
                    ["content"] = JsonNode.Parse(ri.Content), ["seq"] = ri.Seq,
                });
        var ders = new JsonArray();
        foreach (var d in Derived())
            ders.Add(new JsonObject
            {
                ["id"] = d.Id, ["kind"] = d.Kind, ["algo"] = d.Algo,
                ["fingerprint"] = d.Fingerprint, ["sources"] = new JsonArray(d.Sources.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["content"] = JsonNode.Parse(d.Content), ["seq"] = d.Seq,
            });
        var evs = new JsonArray();
        foreach (var e in Events())
            evs.Add(new JsonObject
            {
                ["seq"] = e.Seq, ["type"] = e.Type,
                ["payload"] = JsonNode.Parse(e.Payload), ["at"] = e.At,
            });
        root["rawInputs"] = raws;
        root["derived"] = ders;
        root["events"] = evs;
        return root.ToJsonString();
    }

    public (int Raws, int Derived, int Events) ImportArchive(string archiveJson)
    {
        var root = JsonNode.Parse(archiveJson)!.AsObject();
        if ((string?)root["format"] != "power-sequencer-archive-v1")
            throw new FormatException("unknown archive format");
        var rc = 0; var dc = 0; var ec = 0;
        long maxSeq = 0;
        foreach (var node in root["rawInputs"]!.AsArray())
        {
            var o = node!.AsObject();
            var id = (string)o["id"]!;
            if (GetRaw(id) is not null) continue;
            var seq = (long)o["seq"]!;
            maxSeq = Math.Max(maxSeq, seq);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO raw_inputs(id,chain,version,parent_id,label,content,seq) VALUES($id,$c,$v,$p,$l,$ct,$s)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$c", (string)o["chain"]!);
            cmd.Parameters.AddWithValue("$v", (int)o["version"]!);
            cmd.Parameters.AddWithValue("$p", o["parent"] is null ? DBNull.Value : (string)o["parent"]!);
            cmd.Parameters.AddWithValue("$l", (string)o["label"]!);
            cmd.Parameters.AddWithValue("$ct", o["content"]!.ToJsonString());
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.ExecuteNonQuery();
            rc++;
        }
        foreach (var node in root["derived"]!.AsArray())
        {
            var o = node!.AsObject();
            var id = (string)o["id"]!;
            if (GetDerived(id) is not null) continue;
            var seq = (long)o["seq"]!;
            maxSeq = Math.Max(maxSeq, seq);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO derived(id,kind,algo,fingerprint,sources,content,seq) VALUES($id,$k,$a,$f,$s,$c,$q)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$k", (string)o["kind"]!);
            cmd.Parameters.AddWithValue("$a", (string)o["algo"]!);
            cmd.Parameters.AddWithValue("$f", (string)o["fingerprint"]!);
            cmd.Parameters.AddWithValue("$s", o["sources"]!.ToJsonString());
            cmd.Parameters.AddWithValue("$c", o["content"]!.ToJsonString());
            cmd.Parameters.AddWithValue("$q", seq);
            cmd.ExecuteNonQuery();
            dc++;
        }
        foreach (var node in root["events"]!.AsArray())
        {
            var o = node!.AsObject();
            var seq = (long)o["seq"]!;
            if (Events().Any(e => e.Seq == seq)) continue;
            maxSeq = Math.Max(maxSeq, seq);
            AppendEvent((string)o["type"]!, o["payload"]!.AsObject(), seq, (string?)o["at"]);
            ec++;
        }
        BumpSeqTo(maxSeq);
        return (rc, dc, ec);
    }
}

internal static class JsonHelper
{
    public static string Stringify(string[] values) =>
        System.Text.Json.JsonSerializer.Serialize(values);
}
