using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PowerSequencer.Domain;

namespace PowerSequencer.Persistence;

public sealed record EventRecord(long Seq, string Id, string Project, string Type, JsonNode Payload, string CreatedUtc);
public sealed record InputVersion(string StreamId, int Version, int? ParentVersion, string Kind, JsonNode Data, string Hash, long Seq);
public sealed record ConstraintVersion(string Id, int Version, int? ParentVersion, JsonNode Data, string Fingerprint, long Seq);
public sealed record StoredResult(string Id, string Kind, string? Rail, string AlgoVersion, string Fingerprint,
    List<SourceRef> Sources, JsonNode Payload, string Status, long Seq);
public sealed record StoredDecision(string Id, string Target, string Action, string Rationale,
    bool Adopted, bool Retracted, List<string> ConflictsWith, long Seq);

/// <summary>
/// Append-only event store over SQLite. Events are the source of truth;
/// projections (inputs, constraint versions, results, decisions) are rebuilt
/// by deterministic replay, optionally up to an event position for history.
/// </summary>
public sealed class Store : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly SqliteConnection _db;
    private readonly IClock _clock;

    public Store(string dbPath, IClock clock)
    {
        _clock = clock;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY,
              id TEXT NOT NULL,
              project TEXT NOT NULL,
              type TEXT NOT NULL,
              payload TEXT NOT NULL,
              created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_project_seq ON events(project, seq);
            """;
        cmd.ExecuteNonQuery();
    }

    public bool IsEmpty(string project)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE project = $p";
        cmd.Parameters.AddWithValue("$p", project);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
    }

    public void Clear(string project)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE project = $p";
        cmd.Parameters.AddWithValue("$p", project);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Append an event; seq and stable id are assigned by the store.</summary>
    public EventRecord Append(string project, string type, JsonNode payload)
    {
        using var tx = _db.BeginTransaction();
        var now = _clock.Now.ToString("O");
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events(id, project, type, payload, created_utc) VALUES($id,$p,$t,$pl,$c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$id", "pending");
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$pl", payload.ToJsonString(JsonOptions));
        cmd.Parameters.AddWithValue("$c", now);
        var seq = Convert.ToInt64(cmd.ExecuteScalar());
        var id = $"ev-{seq:D6}";
        using var upd = _db.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE events SET id=$id WHERE seq=$seq";
        upd.Parameters.AddWithValue("$id", id);
        upd.Parameters.AddWithValue("$seq", seq);
        upd.ExecuteNonQuery();
        tx.Commit();
        return new EventRecord(seq, id, project, type, payload, now);
    }

    public List<EventRecord> Events(string project, long? asOf = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = asOf is null
            ? "SELECT seq,id,type,payload,created_utc FROM events WHERE project=$p ORDER BY seq"
            : "SELECT seq,id,type,payload,created_utc FROM events WHERE project=$p AND seq<=$as ORDER BY seq";
        cmd.Parameters.AddWithValue("$p", project);
        if (asOf is not null) cmd.Parameters.AddWithValue("$as", asOf);
        var list = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var seq = r.GetInt64(0);
            list.Add(new EventRecord(seq, r.GetString(1), project, r.GetString(2),
                JsonNode.Parse(r.GetString(3))!, r.GetString(4)));
        }
        return list;
    }

    public long LastSeq(string project)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events WHERE project=$p";
        cmd.Parameters.AddWithValue("$p", project);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Import an archive into an empty project, preserving seq, ids and order.</summary>
    public int ImportEmpty(string project, IReadOnlyList<EventRecord> events)
    {
        if (!IsEmpty(project))
            throw new InvalidOperationException($"project '{project}' already has events; importing an archive requires an empty project");
        using var tx = _db.BeginTransaction();
        foreach (var e in events)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO events(seq,id,project,type,payload,created_utc) VALUES($seq,$id,$p,$t,$pl,$c)";
            cmd.Parameters.AddWithValue("$seq", e.Seq);
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.Parameters.AddWithValue("$p", project);
            cmd.Parameters.AddWithValue("$t", e.Type);
            cmd.Parameters.AddWithValue("$pl", e.Payload.ToJsonString(JsonOptions));
            cmd.Parameters.AddWithValue("$c", e.CreatedUtc);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return events.Count;
    }

    public string ExportJson(string project)
    {
        var events = Events(project).Select(e => new JsonObject
        {
            ["seq"] = e.Seq,
            ["id"] = e.Id,
            ["type"] = e.Type,
            ["payload"] = e.Payload.DeepClone(),
            ["createdUtc"] = e.CreatedUtc
        });
        var archive = new JsonObject
        {
            ["format"] = "powerseq-archive/1",
            ["project"] = project,
            ["exportedUtc"] = _clock.Now.ToString("O"),
            ["events"] = new JsonArray(events.ToArray())
        };
        return archive.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Rebuild projections by deterministic replay (optionally as of a position).</summary>
    public Projections Project(string project, long? asOf = null)
    {
        var p = new Projections();
        foreach (var e in Events(project, asOf))
            p.Apply(e);
        return p;
    }

    public void Dispose() => _db.Dispose();
}

public sealed class Projections
{
    public List<InputVersion> Inputs { get; } = new();
    public List<ConstraintVersion> Constraints { get; } = new();
    public List<StoredResult> Results { get; } = new();
    public List<StoredDecision> Decisions { get; } = new();
    public List<EventRecord> EventLog { get; } = new();

    public ConstraintVersion? LatestConstraints() => Constraints.OrderBy(c => c.Version).LastOrDefault();

    public InputVersion? LatestInput(string streamId) =>
        Inputs.Where(i => i.StreamId == streamId).OrderBy(i => i.Version).LastOrDefault();

    public StoredResult? Result(string id) => Results.FirstOrDefault(r => r.Id == id);

    public void Apply(EventRecord e)
    {
        EventLog.Add(e);
        var pl = e.Payload;
        switch (e.Type)
        {
            case "input.imported":
            case "input.revised":
                Inputs.Add(new InputVersion(
                    (string)pl["streamId"]!,
                    e.Type == "input.imported" ? 1 : (int)pl["parentVersion"]! + 1,
                    e.Type == "input.imported" ? null : (int?)pl["parentVersion"],
                    (string)pl["kind"]!,
                    pl["data"]!.DeepClone(),
                    (string)pl["hash"]!, e.Seq));
                break;
            case "constraints.created":
                var parent = pl["parentVersion"] is null ? (int?)null : (int)pl["parentVersion"]!;
                Constraints.Add(new ConstraintVersion(
                    (string)pl["id"]!,
                    parent is null ? 1 : parent.Value + 1,
                    parent,
                    pl["data"]!.DeepClone(), (string)pl["fingerprint"]!, e.Seq));
                break;
            case "analysis.computed":
                foreach (var r in pl["results"]!.AsArray())
                {
                    var id = (string)r["id"]!;
                    if (Results.Any(x => x.Id == id)) continue; // deterministic id: same value
                    var sources = r["sources"]!.AsArray()
                        .Select(s => new SourceRef((string)s["kind"]!, (string)s["id"]!, (int)s["version"]!)).ToList();
                    Results.Add(new StoredResult(id, (string)r["kind"]!, (string?)r["rail"],
                        (string)r["algo"]!, (string)r["fingerprint"]!, sources, r.DeepClone(),
                        (string?)r["status"] ?? "current", e.Seq));
                }
                break;
            case "result.invalidated":
                foreach (var id in pl["ids"]!.AsArray().Select(v => (string)v!))
                {
                    var res = Results.FirstOrDefault(x => x.Id == id);
                    if (res is not null && res.Status == "current")
                        Results[Results.IndexOf(res)] = res with { Status = "stale" };
                }
                break;
            case "result.revalidated":
                foreach (var id in pl["ids"]!.AsArray().Select(v => (string)v!))
                {
                    var res = Results.FirstOrDefault(x => x.Id == id);
                    if (res is not null && res.Status == "stale")
                        Results[Results.IndexOf(res)] = res with { Status = "current" };
                }
                break;
            case "decision.made":
                Decisions.Add(new StoredDecision(
                    (string)pl["id"]!, (string)pl["target"]!, (string)pl["action"]!,
                    (string)pl["rationale"]!, false, false,
                    pl["conflictsWith"]?.AsArray().Select(v => (string)v!).ToList() ?? new List<string>(),
                    e.Seq));
                RecomputeAdopted();
                break;
            case "decision.retracted":
                var did = (string)pl["id"]!;
                var dec = Decisions.FirstOrDefault(x => x.Id == did);
                if (dec is not null)
                {
                    Decisions[Decisions.IndexOf(dec)] = dec with { Retracted = true, Adopted = false };
                    RecomputeAdopted();
                }
                break;
        }
    }

    private void RecomputeAdopted()
    {
        var targets = Decisions.Where(d => !d.Retracted).Select(d => d.Target).Distinct();
        for (var i = 0; i < Decisions.Count; i++)
        {
            var d = Decisions[i];
            var adopted = !d.Retracted && d.Seq == Decisions
                .Where(x => x.Target == d.Target && !x.Retracted).Max(x => x.Seq);
            if (d.Adopted != adopted)
                Decisions[i] = d with { Adopted = adopted };
        }
    }
}
