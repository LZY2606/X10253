using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PowerSeq.Data;

public interface IClock { DateTimeOffset Now(); }
public sealed class SystemClock : IClock { public DateTimeOffset Now() => DateTimeOffset.UtcNow; }

/// <summary>SQLite-backed event-sourced store. Raw inputs are immutable;
/// revisions are new rows with parent links. Statuses are derived by folding events.</summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IClock _clock;
    private readonly Func<string> _idGen;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public Store(string dbPath, IClock? clock = null, Func<string>? idGen = null)
    {
        _clock = clock ?? new SystemClock();
        _idGen = idGen ?? (() => Guid.NewGuid().ToString("N"));
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sources(
              id TEXT PRIMARY KEY, project TEXT NOT NULL, family TEXT NOT NULL,
              version INTEGER NOT NULL, parent_id TEXT, kind TEXT NOT NULL,
              raw TEXT NOT NULL, created_at TEXT NOT NULL, seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS constraints(
              id TEXT PRIMARY KEY, project TEXT NOT NULL, version INTEGER NOT NULL,
              parent_id TEXT, raw TEXT NOT NULL, fingerprint TEXT NOT NULL,
              changed_rails TEXT NOT NULL, created_at TEXT NOT NULL, seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS results(
              id TEXT PRIMARY KEY, project TEXT NOT NULL, kind TEXT NOT NULL,
              base_status TEXT NOT NULL, algo TEXT NOT NULL, fingerprint TEXT NOT NULL,
              source_ids TEXT NOT NULL, used_rails TEXT NOT NULL,
              payload TEXT NOT NULL, created_at TEXT NOT NULL, seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              id TEXT PRIMARY KEY, project TEXT NOT NULL, seq INTEGER NOT NULL,
              kind TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_events_proj ON events(project, seq);
            """;
        cmd.ExecuteNonQuery();
    }

    public string NewId() => _idGen();
    public string Now() => _clock.Now().ToString("O");

    public int NextSeq(string project)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events WHERE project=$p";
        cmd.Parameters.AddWithValue("$p", project);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public string AppendEvent(string project, string kind, object payload, int? seq = null, string? id = null, string? createdAt = null)
    {
        var eid = id ?? NewId();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO events(id,project,seq,kind,payload,created_at) VALUES($id,$p,$s,$k,$pl,$t)";
        cmd.Parameters.AddWithValue("$id", eid);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$s", seq ?? NextSeq(project));
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$pl", JsonSerializer.Serialize(payload, Json));
        cmd.Parameters.AddWithValue("$t", createdAt ?? Now());
        cmd.ExecuteNonQuery();
        return eid;
    }

    public void InsertSource(string id, string project, string family, int version, string? parentId,
        string kind, string raw, int seq, string? createdAt = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO sources(id,project,family,version,parent_id,kind,raw,created_at,seq) VALUES($id,$p,$f,$v,$pa,$k,$r,$t,$s)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$f", family);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$pa", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$r", raw);
        cmd.Parameters.AddWithValue("$t", createdAt ?? Now());
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
    }

    public void InsertConstraint(string id, string project, int version, string? parentId, string raw,
        string fingerprint, string changedRails, int seq, string? createdAt = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO constraints(id,project,version,parent_id,raw,fingerprint,changed_rails,created_at,seq) VALUES($id,$p,$v,$pa,$r,$fp,$cr,$t,$s)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$pa", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", raw);
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$cr", changedRails);
        cmd.Parameters.AddWithValue("$t", createdAt ?? Now());
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
    }

    public void InsertResult(string id, string project, string kind, string baseStatus, string algo,
        string fingerprint, string sourceIds, string usedRails, string payload, int seq, string? createdAt = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO results(id,project,kind,base_status,algo,fingerprint,source_ids,used_rails,payload,created_at,seq) VALUES($id,$p,$k,$bs,$a,$fp,$si,$ur,$pl,$t,$s)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$bs", baseStatus);
        cmd.Parameters.AddWithValue("$a", algo);
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$si", sourceIds);
        cmd.Parameters.AddWithValue("$ur", usedRails);
        cmd.Parameters.AddWithValue("$pl", payload);
        cmd.Parameters.AddWithValue("$t", createdAt ?? Now());
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
    }

    public List<Dictionary<string, object?>> Query(string sql, params (string, object)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public List<Dictionary<string, object?>> Sources(string project) =>
        Query("SELECT * FROM sources WHERE project=$p ORDER BY seq", ("$p", project));
    public List<Dictionary<string, object?>> Constraints(string project) =>
        Query("SELECT * FROM constraints WHERE project=$p ORDER BY seq", ("$p", project));
    public List<Dictionary<string, object?>> Results(string project) =>
        Query("SELECT * FROM results WHERE project=$p ORDER BY seq", ("$p", project));
    public List<Dictionary<string, object?>> Events(string project) =>
        Query("SELECT * FROM events WHERE project=$p ORDER BY seq", ("$p", project));

    public string? GetMeta(string key)
    {
        var rows = Query("SELECT v FROM meta WHERE k=$k", ("$k", key));
        return rows.Count == 0 ? null : rows[0]["v"]?.ToString();
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
