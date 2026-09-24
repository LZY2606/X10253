using System.Text.Json;
using System.Text.Json.Nodes;
using PowerSequencer.App;
using PowerSequencer.Domain;
using PowerSequencer.Persistence;
using PowerSequencer.Web;

if (args.Contains("--emit-casepack"))
{
    var outPath = args.Skip(args.IndexOf("--emit-casepack") + 1).FirstOrDefault()
        ?? Path.Combine(Directory.GetCurrentDirectory(), "casepack", "power-sequencing-case.json");
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, CasePack.GenerateArchive());
    Console.WriteLine($"case pack written: {outPath}");
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var project = CasePack.Project;
var dbPath = Environment.GetEnvironmentVariable("POWERSEQ_DB")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "powerseq.db");
var store = new Store(dbPath, new SystemClock());

// Local-first: seed the bundled offline case pack into an empty database.
if (store.IsEmpty(project))
{
    string? archive = null;
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "casepack", "power-sequencing-case.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "casepack", "power-sequencing-case.json"),
        Path.Combine(builder.Environment.ContentRootPath, "casepack", "power-sequencing-case.json"),
    };
    foreach (var c in candidates)
        if (File.Exists(c)) { archive = File.ReadAllText(c); break; }
    archive ??= CasePack.GenerateArchive();
    ImportArchive(store, project, archive, appendMarker: true);
    Console.WriteLine($"seeded bundled case pack into {dbPath}");
}

string? Form(HttpRequest r, string key) => r.Form[key].ToString();

(string? error, WaveformData? wf) ParseWaveform(string json)
{
    try
    {
        var wf = JsonSerializer.Deserialize<WaveformData>(json, Store.JsonOptions);
        if (wf is null) return ("payload is null", null);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(wf.Rail)) errors.Add("missing 'rail'");
        if (!new[] { "voltage", "enable", "good" }.Contains(wf.SignalKind))
            errors.Add("'signalKind' must be one of voltage|enable|good");
        if (wf.Samples is null || wf.Samples.Count < 2) errors.Add("'samples' needs at least 2 points");
        if (wf.SampleClock is null || wf.ClockJitter is null || wf.MeasurementError is null)
            errors.Add("sampleClock, clockJitter and measurementError are required (with units)");
        wf.Samples?.ForEach(s =>
        {
            if (s?.Time is null || s.Value is null || string.IsNullOrWhiteSpace(s.Time.Unit) || string.IsNullOrWhiteSpace(s.Value.Unit))
                errors.Add("every sample needs {time:{value,unit}, value:{value,unit}}");
        });
        if (errors.Count > 0) return (string.Join("; ", errors), null);
        return (null, wf);
    }
    catch (JsonException ex) { return ($"invalid JSON: {ex.Message}", null); }
}

app.MapGet("/", () => Results.Content(Pages.Dashboard(store, project), "text/html; charset=utf-8"));

app.MapGet("/review", (HttpRequest req) =>
{
    int? asOf = int.TryParse(req.Query["asOf"], out var a) ? a : null;
    return Results.Content(Pages.Review(store, project, asOf), "text/html; charset=utf-8");
});

app.MapPost("/import/waveform", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var json = form["json"].ToString();
    var (error, wf) = ParseWaveform(json);
    if (error is not null || wf is null)
        return Results.Content(Pages.ImportError(error ?? "unknown parse error", json), "text/html; charset=utf-8");
    new Recorder(store, project).ImportWaveform(wf);
    return Results.Redirect("/");
});

app.MapPost("/import/revise", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var streamId = form["streamId"].ToString();
    var json = form["json"].ToString();
    var (error, wf) = ParseWaveform(json);
    if (error is not null || wf is null)
        return Results.Content(Pages.ImportError(error ?? "unknown parse error", json), "text/html; charset=utf-8");
    try
    {
        new Recorder(store, project).ReviseWaveform(streamId, wf);
    }
    catch (Exception ex) { return Results.Content(Pages.ImportError(ex.Message, json), "text/html; charset=utf-8"); }
    return Results.Redirect("/");
});

app.MapPost("/fork", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var parentId = form["parentId"].ToString();
    var json = form["json"].ToString();
    try
    {
        var cs = JsonSerializer.Deserialize<ConstraintSet>(json, Store.JsonOptions)
            ?? throw new InvalidOperationException("payload is null");
        new Recorder(store, project).ForkConstraints(parentId, cs);
    }
    catch (Exception ex) { return Results.Content(Pages.ImportError(ex.Message, json), "text/html; charset=utf-8"); }
    return Results.Redirect("/review");
});

app.MapPost("/decide", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    new Recorder(store, project).Decide(form["target"], form["action"], form["rationale"]);
    var ret = form["returnUrl"].ToString();
    return Results.Redirect(string.IsNullOrWhiteSpace(ret) ? "/review" : ret);
});

app.MapPost("/decision/retract", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    new Recorder(store, project).RetractDecision(form["id"]);
    return Results.Redirect("/review");
});

app.MapGet("/trace/{id}", (string id) =>
    Results.Content(Pages.Trace(store, project, id), "text/html; charset=utf-8"));

app.MapGet("/events", () =>
    Results.Content(Pages.Events(store, project), "text/html; charset=utf-8"));

app.MapGet("/inputs/{streamId}", (string streamId) =>
    Results.Content(Pages.InputDetail(store, project, streamId), "text/html; charset=utf-8"));

app.MapGet("/samples", () =>
    Results.Content(Pages.SamplesPage(SampleData.Valid, SampleData.Malformed), "text/html; charset=utf-8"));

app.MapGet("/export", () =>
    Results.File(System.Text.Encoding.UTF8.GetBytes(store.ExportJson(project)),
        "application/json", "powerseq-evidence-room.json"));

app.MapPost("/import/archive", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var json = form["archive"].ToString();
    try
    {
        var n = ImportArchive(store, project, json, appendMarker: true);
        Console.WriteLine($"imported archive with {n} events");
    }
    catch (Exception ex)
    {
        return Results.Content(Pages.ImportError(ex.Message, json), "text/html; charset=utf-8");
    }
    return Results.Redirect("/");
});

app.MapPost("/project/reset", () =>
{
    store.Clear(project);
    return Results.Redirect("/");
});

app.Run();
return 0;

static int ImportArchive(Store store, string project, string json, bool appendMarker)
{
    JsonNode? root;
    try { root = JsonNode.Parse(json); }
    catch (JsonException ex) { throw new InvalidOperationException($"invalid archive JSON: {ex.Message}"); }
    if ((string?)root?["format"] != "powerseq-archive/1")
        throw new InvalidOperationException("archive must declare \"format\": \"powerseq-archive/1\"");
    var arr = root!["events"] as JsonArray
        ?? throw new InvalidOperationException("archive has no events array");
    var events = new List<EventRecord>();
    foreach (var e in arr)
    {
        events.Add(new EventRecord((long)e!["seq"]!, (string)e["id"]!, project,
            (string)e["type"]!, e["payload"]!.DeepClone(), (string)e["createdUtc"]!));
    }
    var n = store.ImportEmpty(project, events);
    if (appendMarker)
        store.Append(project, "archive.imported", new JsonObject
        {
            ["count"] = n,
            ["exportedUtc"] = (string?)root["exportedUtc"]
        });
    return n;
}
