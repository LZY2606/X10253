using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PowerSeq.Core;

public static class Algo
{
    public const string Version = "1.0.0";
}

public sealed record EventRecord(long Seq, string Ts, string Actor, string Type, string Payload);

public sealed record CaseInfo(string CaseId, string Title, long LatestSeq);

// ---- 事件负载 ----
public sealed record CaseCreatedPayload(string Title, ClockSpec Clock);
public sealed record SourcePayload(IReadOnlyList<RawSample> Samples, string? Note);
public sealed record SourceImportedPayload(string SourceId, string Name, SourceKind Kind, string Rail, string VersionId, SourcePayload Payload);
public sealed record SourceRevisedPayload(string SourceId, string VersionId, string ParentVersionId, SourcePayload Payload);
public sealed record ThresholdsImportedPayload(string SetId, string VersionId, ThresholdPayload Payload, string? Note);
public sealed record ThresholdsForkedPayload(string SetId, string VersionId, string ParentVersionId, ThresholdPayload Payload, string? Note);
public sealed record DependenciesImportedPayload(string VersionId, string? ParentVersionId, DependencyPayload Payload);
public sealed record DecisionMadePayload(string DecisionId, string CandidateId, Verdict Verdict, string Rationale);
public sealed record DecisionRevertedPayload(string DecisionId, string Reason);
public sealed record ConflictResolvedPayload(string CandidateId, string ChosenDecisionId, string Rationale);

// ---- 视图 ----
public sealed record SourceVersionView(string VersionId, string? ParentVersionId, int Version, string? Note, long Seq, SourcePayload Payload);
public sealed record SourceView(
    string SourceId, string Name, SourceKind Kind, string Rail,
    IReadOnlyList<SourceVersionView> Versions, string CurrentVersionId,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public sealed record ThresholdVersionView(string VersionId, string? ParentVersionId, int Version, string? Note, long Seq, ThresholdPayload Payload);
public sealed record ThresholdSetView(string SetId, IReadOnlyList<ThresholdVersionView> Versions, string CurrentVersionId);

public sealed record DependencyVersionView(string VersionId, string? ParentVersionId, int Version, long Seq, DependencyPayload Payload);

public sealed record DerivedResult(
    string Id, string Kind, string Subject, string AlgoVersion, string ParamsFingerprint,
    IReadOnlyDictionary<string, string> Inputs, string PayloadJson, bool Current, string? StaleReason);

public sealed record DecisionView(
    string DecisionId, string CandidateId, Verdict Verdict, string Rationale,
    string Actor, long Seq, bool Reverted, bool IsResolution);

public sealed record ConflictView(string CandidateId, IReadOnlyList<string> DecisionIds, bool Resolved, string? ChosenDecisionId);

public sealed record CandidateView(Candidate Candidate, CandidateStatus Status, IReadOnlyList<string> DecisionIds);

public sealed record CaseView(
    string CaseId, string Title, ClockSpec? Clock, long AtSeq, long LatestSeq,
    IReadOnlyList<SourceView> Sources,
    IReadOnlyList<ThresholdSetView> ThresholdSets,
    IReadOnlyList<DependencyVersionView> DependencyVersions,
    string? CurrentDependencyVersionId,
    IReadOnlyList<DerivedResult> Derived,
    IReadOnlyList<DecisionView> Decisions,
    IReadOnlyList<CandidateView> Candidates,
    IReadOnlyList<ConflictView> Conflicts,
    IReadOnlyList<EventRecord> Events)
{
    public DerivedResult? CurrentDerived(string kind, string subject) =>
        Derived.FirstOrDefault(d => d.Kind == kind && d.Subject == subject && d.Current);

    public IEnumerable<DerivedResult> CurrentOfKind(string kind) =>
        Derived.Where(d => d.Kind == kind && d.Current);
}

/// <summary>
/// SQLite 事件存储：所有变更以追加事件落库；投影可重放到任意 seq，
/// 回到旧事件即恢复当时的依赖版本与可见结论。
/// </summary>
public sealed class CaseStore
{
    private readonly string _dbPath;
    private readonly IClock _clock;

    public CaseStore(string dbPath, IClock clock)
    {
        _dbPath = dbPath;
        _clock = clock;
        Initialize();
    }

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events(
              case_id TEXT NOT NULL,
              seq INTEGER NOT NULL,
              ts TEXT NOT NULL,
              actor TEXT NOT NULL,
              type TEXT NOT NULL,
              payload TEXT NOT NULL,
              PRIMARY KEY(case_id, seq));
            """;
        cmd.ExecuteNonQuery();
    }

    public long Append(string caseId, string actor, string type, object payload)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(case_id, seq, ts, actor, type, payload)
            VALUES ($c, (SELECT COALESCE(MAX(seq),0)+1 FROM events WHERE case_id=$c), $ts, $a, $t, $p);
            SELECT seq FROM events WHERE case_id=$c AND rowid=last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$c", caseId);
        cmd.Parameters.AddWithValue("$ts", _clock.Now().ToString("O"));
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(payload, JsonUtil.Options));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public List<EventRecord> ReadEvents(string caseId, long? atSeq = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = atSeq is null
            ? "SELECT seq, ts, actor, type, payload FROM events WHERE case_id=$c ORDER BY seq"
            : "SELECT seq, ts, actor, type, payload FROM events WHERE case_id=$c AND seq<=$s ORDER BY seq";
        cmd.Parameters.AddWithValue("$c", caseId);
        if (atSeq is not null) cmd.Parameters.AddWithValue("$s", atSeq.Value);
        var list = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    public List<CaseInfo> ListCases()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.case_id,
                   (SELECT json_extract(payload,'$.title') FROM events
                     WHERE case_id=e.case_id AND type='case-created' ORDER BY seq LIMIT 1) AS title,
                   MAX(seq) FROM events e GROUP BY e.case_id ORDER BY e.case_id;
            """;
        var list = new List<CaseInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CaseInfo(r.GetString(0), r.IsDBNull(1) ? r.GetString(0) : r.GetString(1), r.GetInt64(2)));
        return list;
    }

    // ---- 领域操作 ----

    public void CreateCase(string caseId, string actor, string title, ClockSpec clock) =>
        Append(caseId, actor, "case-created", new CaseCreatedPayload(title, clock));

    public string ImportSource(string caseId, string actor, string sourceId, string name, SourceKind kind, string rail, SourcePayload payload)
    {
        var versionId = Ids.ContentId("sv", JsonUtil.Canon(payload));
        Append(caseId, actor, "source-imported", new SourceImportedPayload(sourceId, name, kind, rail, versionId, payload));
        return versionId;
    }

    public string ReviseSource(string caseId, string actor, string sourceId, string parentVersionId, SourcePayload payload)
    {
        var versionId = Ids.ContentId("sv", JsonUtil.Canon(payload));
        Append(caseId, actor, "source-revised", new SourceRevisedPayload(sourceId, versionId, parentVersionId, payload));
        return versionId;
    }

    public string ImportThresholds(string caseId, string actor, string setId, ThresholdPayload payload, string? note)
    {
        var versionId = Ids.ContentId("tv", JsonUtil.Canon(payload));
        Append(caseId, actor, "thresholds-imported", new ThresholdsImportedPayload(setId, versionId, payload, note));
        return versionId;
    }

    public string ForkThresholds(string caseId, string actor, string setId, string parentVersionId, ThresholdPayload payload, string? note)
    {
        var versionId = Ids.ContentId("tv", JsonUtil.Canon(payload));
        Append(caseId, actor, "thresholds-forked", new ThresholdsForkedPayload(setId, versionId, parentVersionId, payload, note));
        return versionId;
    }

    public string ImportDependencies(string caseId, string actor, string? parentVersionId, DependencyPayload payload)
    {
        var versionId = Ids.ContentId("dv", JsonUtil.Canon(payload));
        Append(caseId, actor, "dependencies-imported", new DependenciesImportedPayload(versionId, parentVersionId, payload));
        return versionId;
    }

    public string Decide(string caseId, string actor, string candidateId, Verdict verdict, string rationale)
    {
        var seq = ReadEvents(caseId).Count + 1L;
        var decisionId = Ids.ContentId("dec", $"{candidateId}|{verdict}|{rationale}|{actor}|{seq}");
        Append(caseId, actor, "decision-made", new DecisionMadePayload(decisionId, candidateId, verdict, rationale));
        return decisionId;
    }

    public void RevertDecision(string caseId, string actor, string decisionId, string reason) =>
        Append(caseId, actor, "decision-reverted", new DecisionRevertedPayload(decisionId, reason));

    public void ResolveConflict(string caseId, string actor, string candidateId, string chosenDecisionId, string rationale) =>
        Append(caseId, actor, "conflict-resolved", new ConflictResolvedPayload(candidateId, chosenDecisionId, rationale));

    // ---- 导出 / 导入 ----

    public string Export(string caseId)
    {
        var events = ReadEvents(caseId);
        var doc = new
        {
            format = "powerseq-archive/1",
            caseId,
            exportedSeq = events.Count,
            events = events.Select(e => new { e.Seq, e.Ts, e.Actor, e.Type, e.Payload }).ToList()
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonUtil.Options) { WriteIndented = true });
    }

    /// <summary>导入档案：保持事件身份、顺序与来源关系；同案例已存在则先清除再按原序恢复。</summary>
    public string Import(string archiveJson)
    {
        using var doc = JsonDocument.Parse(archiveJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != "powerseq-archive/1")
            throw new FormatException("archive format must be powerseq-archive/1");
        var caseId = root.GetProperty("caseId").GetString()
            ?? throw new FormatException("archive missing caseId");
        var events = root.GetProperty("events").EnumerateArray()
            .Select(e => new EventRecord(
                e.GetProperty("seq").GetInt64(),
                e.GetProperty("ts").GetString()!,
                e.GetProperty("actor").GetString()!,
                e.GetProperty("type").GetString()!,
                e.GetProperty("payload").GetRawText()))
            .OrderBy(e => e.Seq).ToList();
        for (var i = 0; i < events.Count; i++)
            if (events[i].Seq != i + 1)
                throw new FormatException($"archive event sequence broken at seq {events[i].Seq}");

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM events WHERE case_id=$c";
            del.Parameters.AddWithValue("$c", caseId);
            del.ExecuteNonQuery();
        }
        foreach (var e in events)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO events(case_id, seq, ts, actor, type, payload) VALUES ($c,$s,$ts,$a,$t,$p)";
            ins.Parameters.AddWithValue("$c", caseId);
            ins.Parameters.AddWithValue("$s", e.Seq);
            ins.Parameters.AddWithValue("$ts", e.Ts);
            ins.Parameters.AddWithValue("$a", e.Actor);
            ins.Parameters.AddWithValue("$t", e.Type);
            ins.Parameters.AddWithValue("$p", e.Payload);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return caseId;
    }
}
