using Microsoft.Data.Sqlite;

namespace PowerSeq.Core;

/// <summary>
/// Append-only SQLite store. Raw inputs are immutable once submitted; revisions are new
/// versions linked by parent ids. Events, results and decisions keep stable identities
/// across export / delete / import.
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _conn;

    public Store(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        Migrate();
    }

    public static Store InMemory()
    {
        var s = new Store(":memory:");
        return s;
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS inputs(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              kind TEXT NOT NULL, id TEXT NOT NULL, parent_id TEXT,
              json TEXT NOT NULL, created_at TEXT NOT NULL,
              UNIQUE(kind, id));
            CREATE TABLE IF NOT EXISTS results(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, alg TEXT NOT NULL,
              fingerprint TEXT NOT NULL, sources TEXT NOT NULL, scope TEXT NOT NULL,
              payload TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE, ts TEXT NOT NULL, type TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS decisions(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE, target TEXT NOT NULL, verdict TEXT NOT NULL,
              rationale TEXT NOT NULL, author TEXT NOT NULL, created_at TEXT NOT NULL,
              undone_by TEXT);
            CREATE TABLE IF NOT EXISTS active(kind TEXT PRIMARY KEY, id TEXT NOT NULL);
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql, params (string name, object? value)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---- inputs (immutable) ----

    /// <summary>Returns false if the exact version already exists (idempotent). Throws on attempted overwrite with different content — impossible with content ids, guarded anyway.</summary>
    public bool InsertInput(string kind, string id, string? parentId, string json, string createdAt)
    {
        var existing = Scalar("SELECT json FROM inputs WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id)) as string;
        if (existing is not null)
        {
            if (existing != json) throw new InvalidOperationException($"input {kind}/{id} already exists with different content; raw inputs are immutable");
            return false;
        }
        Exec("INSERT INTO inputs(kind,id,parent_id,json,created_at) VALUES($k,$i,$p,$j,$c)",
            ("$k", kind), ("$i", id), ("$p", parentId), ("$j", json), ("$c", createdAt));
        SetActive(kind, id);
        return true;
    }

    public List<InputRecord> Inputs(string? kind = null) => InputsUpTo(long.MaxValue, kind);

    public List<InputRecord> InputsUpTo(long maxSeq, string? kind = null)
    {
        var sql = kind is null ? "SELECT kind,id,parent_id,json,created_at,seq FROM inputs WHERE seq<=$m ORDER BY seq"
                               : "SELECT kind,id,parent_id,json,created_at,seq FROM inputs WHERE kind=$k AND seq<=$m ORDER BY seq";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$m", maxSeq);
        if (kind is not null) cmd.Parameters.AddWithValue("$k", kind);
        var list = new List<InputRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new InputRecord(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
        return list;
    }

    public string? GetInputJson(string kind, string id)
        => Scalar("SELECT json FROM inputs WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id)) as string;

    // ---- active version pointers ----

    public void SetActive(string kind, string id)
        => Exec("INSERT INTO active(kind,id) VALUES($k,$i) ON CONFLICT(kind) DO UPDATE SET id=$i", ("$k", kind), ("$i", id));

    public string? ActiveId(string kind)
        => Scalar("SELECT id FROM active WHERE kind=$k", ("$k", kind)) as string;

    public Dictionary<string, string> ActiveMap()
    {
        var map = new Dictionary<string, string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT kind,id FROM active";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    // ---- results ----

    public bool InsertResult(DerivedResult r)
    {
        var existing = Scalar("SELECT id FROM results WHERE id=$i", ("$i", r.Id)) as string;
        if (existing is not null) return false;
        Exec("INSERT INTO results(id,kind,alg,fingerprint,sources,scope,payload,created_at) VALUES($i,$k,$a,$f,$s,$c,$p,$t)",
            ("$i", r.Id), ("$k", r.Kind), ("$a", r.AlgorithmVersion), ("$f", r.ParamFingerprint),
            ("$s", Canon.Json(r.SourceIds)), ("$c", Canon.Json(r.Scope)), ("$p", r.PayloadJson), ("$t", r.CreatedAt));
        return true;
    }

    public List<DerivedResult> Results() => ResultsUpTo(long.MaxValue).Select(x => x.Result).ToList();

    public List<(DerivedResult Result, long Seq)> ResultsUpTo(long maxSeq)
    {
        var list = new List<(DerivedResult, long)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,kind,alg,fingerprint,sources,scope,payload,created_at,seq FROM results WHERE seq<=$m ORDER BY seq";
        cmd.Parameters.AddWithValue("$m", maxSeq);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((new DerivedResult(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                Canon.Parse<List<string>>(r.GetString(4)), Canon.Parse<List<RailScope>>(r.GetString(5)),
                r.GetString(6), r.GetString(7)), r.GetInt64(8)));
        return list;
    }

    // ---- events (append-only) ----

    public EventRecord AppendEvent(string ts, string type, object payload)
    {
        var json = Canon.Json(payload);
        var id = Ids.Compute("ev", $"{type}:{json}:{Scalar("SELECT COUNT(*) FROM events")}");
        Exec("INSERT INTO events(id,ts,type,json) VALUES($i,$t,$y,$j)", ("$i", id), ("$t", ts), ("$y", type), ("$j", json));
        var seq = (long)(Scalar("SELECT MAX(seq) FROM events") ?? 0L);
        return new EventRecord(seq, id, ts, type, json);
    }

    public List<EventRecord> Events()
    {
        var list = new List<EventRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seq,id,ts,type,json FROM events ORDER BY seq";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    // ---- decisions ----

    public bool InsertDecision(Decision d)
    {
        var existing = Scalar("SELECT id FROM decisions WHERE id=$i", ("$i", d.Id)) as string;
        if (existing is not null) return false;
        Exec("INSERT INTO decisions(id,target,verdict,rationale,author,created_at,undone_by) VALUES($i,$t,$v,$r,$a,$c,NULL)",
            ("$i", d.Id), ("$t", d.TargetResultId), ("$v", d.Verdict), ("$r", d.Rationale), ("$a", d.Author), ("$c", d.CreatedAt));
        return true;
    }

    public void MarkUndone(string decisionId, string undoEventId)
        => Exec("UPDATE decisions SET undone_by=$u WHERE id=$i", ("$u", undoEventId), ("$i", decisionId));

    public List<Decision> Decisions()
    {
        var list = new List<Decision>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id,target,verdict,rationale,author,created_at,undone_by FROM decisions ORDER BY seq";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Decision(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // ---- export / import ----

    public string Export()
    {
        var doc = new
        {
            format = "powerseq-archive-v1",
            inputs = Inputs().Select(i => new { i.Kind, i.Id, i.ParentId, i.Json, i.CreatedAt }).ToList(),
            results = Results(),
            events = Events(),
            decisions = Decisions(),
            active = ActiveMap(),
        };
        return Canon.Json(doc);
    }

    public void ImportArchive(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var i in root.GetProperty("inputs").EnumerateArray())
            InsertInput(i.GetProperty("kind").GetString()!, i.GetProperty("id").GetString()!,
                i.GetProperty("parentId").ValueKind == System.Text.Json.JsonValueKind.Null ? null : i.GetProperty("parentId").GetString(),
                i.GetProperty("json").GetString()!, i.GetProperty("createdAt").GetString()!);
        foreach (var r in root.GetProperty("results").EnumerateArray())
            InsertResult(Canon.Parse<DerivedResult>(r.GetRawText()));
        foreach (var e in root.GetProperty("events").EnumerateArray())
            Exec("INSERT OR IGNORE INTO events(id,ts,type,json) VALUES($i,$t,$y,$j)",
                ("$i", e.GetProperty("id").GetString()!), ("$t", e.GetProperty("ts").GetString()!),
                ("$y", e.GetProperty("type").GetString()!), ("$j", e.GetProperty("json").GetString()!));
        foreach (var d in root.GetProperty("decisions").EnumerateArray())
            InsertDecision(Canon.Parse<Decision>(d.GetRawText()));
        foreach (var kv in root.GetProperty("active").EnumerateObject())
            SetActive(kv.Name, kv.Value.GetString()!);
    }

    private object? Scalar(string sql, params (string name, object? value)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _conn.Dispose();
}
