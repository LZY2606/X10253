using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EvidenceRoom;

/// <summary>Virtual clock: tests inject a fixed time; the app uses wall time.</summary>
public interface IClock { DateTimeOffset Now(); }
public sealed class SystemClock : IClock { public DateTimeOffset Now() => DateTimeOffset.UtcNow; }
public sealed class FixedClock(DateTimeOffset at) : IClock
{
    private DateTimeOffset _at = at;
    public DateTimeOffset Now() => _at;
    public void Advance(TimeSpan dt) => _at += dt;
}

/// <summary>Local-first SQLite store. Raw inputs immutable; derived results carry provenance; events append-only.</summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;
    public IClock Clock { get; }

    public Store(string path, IClock? clock = null)
    {
        Clock = clock ?? new SystemClock();
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS inputs(
              id TEXT PRIMARY KEY, parent_id TEXT, kind TEXT NOT NULL,
              label TEXT NOT NULL, payload TEXT NOT NULL, seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS results(
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, algo TEXT NOT NULL,
              fingerprint TEXT NOT NULL, sources TEXT NOT NULL,
              status TEXT NOT NULL, payload TEXT NOT NULL, seq INTEGER NOT NULL,
              event_seq INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL, at TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();

    private long NextSeq(string table)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(seq),0)+1 FROM {table}";
        return (long)(cmd.ExecuteScalar() ?? 1L);
    }

    // ---------- inputs (immutable, versioned) ----------

    public InputVersion AddInput(string kind, string label, string payloadJson, string? parentId = null)
    {
        // id is content-addressed: identical re-import yields identical identity
        var id = Ids.Stable("in", $"{kind}|{parentId}|{payloadJson}");
        var existing = GetInput(id);
        if (existing is not null) return existing;
        var seq = NextSeq("inputs");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO inputs(id,parent_id,kind,label,payload,seq) VALUES($i,$p,$k,$l,$j,$s)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$j", payloadJson);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
        return new InputVersion(id, parentId, kind, label, payloadJson, seq);
    }

    public InputVersion? GetInput(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,parent_id,kind,label,payload,seq FROM inputs WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new InputVersion(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
            r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5));
    }

    public List<InputVersion> Inputs(string? kind = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = kind is null
            ? "SELECT id,parent_id,kind,label,payload,seq FROM inputs ORDER BY seq"
            : "SELECT id,parent_id,kind,label,payload,seq FROM inputs WHERE kind=$k ORDER BY seq";
        if (kind is not null) cmd.Parameters.AddWithValue("$k", kind);
        var list = new List<InputVersion>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new InputVersion(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
        return list;
    }

    public InputVersion? LatestInput(string kind) => Inputs(kind).LastOrDefault();

    // ---------- derived results ----------

    public record Source(string Kind, string? Rail, string Id);

    public DerivedResult PutResult(string kind, string fingerprint, List<Source> sources, string payloadJson, long eventSeq = 0)
    {
        var sourcesJson = J.Ser(sources.Select(s => new { kind = s.Kind, rail = s.Rail, id = s.Id }).ToList());
        var id = Ids.Stable("rs", $"{kind}|{fingerprint}|{sourcesJson}|{payloadJson}");
        using (var check = _db.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM results WHERE id=$i";
            check.Parameters.AddWithValue("$i", id);
            if ((long)(check.ExecuteScalar() ?? 0L) > 0) return GetResult(id)!; // replay-safe
        }
        var seq = NextSeq("results");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO results(id,kind,algo,fingerprint,sources,status,payload,seq,event_seq) VALUES($i,$k,$a,$f,$s,'current',$p,$q,$e)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$a", Engine.AlgoVersion);
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$s", sourcesJson);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$q", seq);
        cmd.Parameters.AddWithValue("$e", eventSeq);
        cmd.ExecuteNonQuery();
        return GetResult(id)!;
    }

    public DerivedResult? GetResult(string id) => Results().FirstOrDefault(r => r.Id == id);

    public List<DerivedResult> Results(string? kind = null, long? asOfEventSeq = null)
    {
        using var cmd = _db.CreateCommand();
        var sql = "SELECT id,kind,algo,fingerprint,sources,status,payload,seq,event_seq FROM results WHERE 1=1";
        if (kind is not null) sql += " AND kind=$k";
        if (asOfEventSeq is not null) sql += " AND event_seq<=$q";
        sql += " ORDER BY seq";
        cmd.CommandText = sql;
        if (kind is not null) cmd.Parameters.AddWithValue("$k", kind);
        if (asOfEventSeq is not null) cmd.Parameters.AddWithValue("$q", asOfEventSeq.Value);
        var list = new List<DerivedResult>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DerivedResult(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetInt64(8)));
        return list;
    }

    /// <summary>Local change: only results whose direct sources intersect the changed scopes go stale.</summary>
    public List<string> MarkStale(string inputKind, HashSet<string>? changedRails)
    {
        var stale = new List<string>();
        foreach (var r in Results())
        {
            if (r.Status != "current") continue;
            var sources = J.De<List<Source>>(r.SourcesJson) ?? new();
            var hit = sources.Any(s =>
                s.Kind == inputKind && (s.Rail is null || changedRails is null || changedRails.Contains(s.Rail)));
            if (!hit) continue;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE results SET status='stale' WHERE id=$i";
            cmd.Parameters.AddWithValue("$i", r.Id);
            cmd.ExecuteNonQuery();
            stale.Add(r.Id);
        }
        return stale;
    }

    // ---------- events (append-only) ----------

    public EventRecord AppendEvent(string kind, object payload)
    {
        var payloadJson = J.Ser(J.Elem(payload));
        var at = Clock.Now().ToString("o");
        var id = Ids.Stable("ev", $"{kind}|{at}|{payloadJson}|{Guid.NewGuid()}");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO events(id,at,kind,payload) VALUES($i,$a,$k,$p)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$a", at);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.ExecuteNonQuery();
        using var q = _db.CreateCommand();
        q.CommandText = "SELECT seq FROM events WHERE id=$i";
        q.Parameters.AddWithValue("$i", id);
        return new EventRecord((long)(q.ExecuteScalar() ?? 0L), id, at, kind, payloadJson);
    }

    public List<EventRecord> Events(long? upToSeq = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq,id,at,kind,payload FROM events ORDER BY seq"
            : "SELECT seq,id,at,kind,payload FROM events WHERE seq<=$s ORDER BY seq";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$s", upToSeq.Value);
        var list = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    // ---------- export / import (identity, order, provenance preserved) ----------

    public string Export()
    {
        var doc = new
        {
            format = "evidence-room-export/1",
            inputs = Inputs().Select(i => new { i.Id, i.ParentId, i.Kind, i.Label, payload = i.PayloadJson, i.Seq }),
            results = Results().Select(r => new { r.Id, r.Kind, algo = r.AlgoVersion, r.Fingerprint,
                sources = r.SourcesJson, r.Status, payload = r.PayloadJson, r.Seq, r.EventSeq }),
            events = Events().Select(e => new { e.Seq, e.Id, e.At, e.Kind, payload = e.PayloadJson })
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Import(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var f) || f.GetString() != "evidence-room-export/1")
            throw new ImportParser.FormatException("导出格式错误",
                new() { "format 必须是 evidence-room-export/1" });
        using var tx = _db.BeginTransaction();
        foreach (var i in root.GetProperty("inputs").EnumerateArray())
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO inputs(id,parent_id,kind,label,payload,seq) VALUES($i,$p,$k,$l,$j,$s)";
            cmd.Parameters.AddWithValue("$i", i.GetProperty("Id").GetString()!);
            cmd.Parameters.AddWithValue("$p", i.TryGetProperty("ParentId", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()! : DBNull.Value);
            cmd.Parameters.AddWithValue("$k", i.GetProperty("Kind").GetString()!);
            cmd.Parameters.AddWithValue("$l", i.GetProperty("Label").GetString()!);
            cmd.Parameters.AddWithValue("$j", i.GetProperty("payload").GetString()!);
            cmd.Parameters.AddWithValue("$s", i.GetProperty("Seq").GetInt64());
            cmd.ExecuteNonQuery();
        }
        foreach (var r in root.GetProperty("results").EnumerateArray())
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO results(id,kind,algo,fingerprint,sources,status,payload,seq,event_seq) VALUES($i,$k,$a,$f,$s,$t,$p,$q,$e)";
            cmd.Parameters.AddWithValue("$i", r.GetProperty("Id").GetString()!);
            cmd.Parameters.AddWithValue("$k", r.GetProperty("Kind").GetString()!);
            cmd.Parameters.AddWithValue("$a", r.GetProperty("algo").GetString()!);
            cmd.Parameters.AddWithValue("$f", r.GetProperty("Fingerprint").GetString()!);
            cmd.Parameters.AddWithValue("$s", r.GetProperty("sources").GetString()!);
            cmd.Parameters.AddWithValue("$t", r.GetProperty("Status").GetString()!);
            cmd.Parameters.AddWithValue("$p", r.GetProperty("payload").GetString()!);
            cmd.Parameters.AddWithValue("$q", r.GetProperty("Seq").GetInt64());
            cmd.Parameters.AddWithValue("$e", r.TryGetProperty("EventSeq", out var es) ? es.GetInt64() : 0);
            cmd.ExecuteNonQuery();
        }
        foreach (var e in root.GetProperty("events").EnumerateArray())
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO events(seq,id,at,kind,payload) VALUES($s,$i,$a,$k,$p)";
            cmd.Parameters.AddWithValue("$s", e.GetProperty("Seq").GetInt64());
            cmd.Parameters.AddWithValue("$i", e.GetProperty("Id").GetString()!);
            cmd.Parameters.AddWithValue("$a", e.GetProperty("At").GetString()!);
            cmd.Parameters.AddWithValue("$k", e.GetProperty("Kind").GetString()!);
            cmd.Parameters.AddWithValue("$p", e.GetProperty("payload").GetString()!);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Reset()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM events; DELETE FROM results; DELETE FROM inputs; DELETE FROM meta;";
        cmd.ExecuteNonQuery();
    }
}
