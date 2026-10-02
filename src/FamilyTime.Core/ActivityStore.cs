using System.Globalization;
using System.Text.Json;

namespace FamilyTime.Core;

public sealed partial class ActivityStore : IDisposable
{
    private readonly object gate = new();
    private readonly Sqlite db;
    public ActivityStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new Sqlite(path);
        db.Run("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS segments (id INTEGER PRIMARY KEY, start REAL NOT NULL, finish REAL NOT NULL, kind TEXT NOT NULL, app TEXT NOT NULL, name TEXT NOT NULL, domain TEXT NOT NULL, category TEXT NOT NULL, background INTEGER NOT NULL)");
        db.Run("CREATE INDEX IF NOT EXISTS segment_time ON segments(start,finish)");
        db.Run("CREATE TABLE IF NOT EXISTS daily (day TEXT NOT NULL, kind TEXT NOT NULL, app TEXT NOT NULL, name TEXT NOT NULL, domain TEXT NOT NULL, category TEXT NOT NULL, background INTEGER NOT NULL, seconds REAL NOT NULL, PRIMARY KEY(day,kind,app,name,domain,category,background))");
        db.Run("CREATE TABLE IF NOT EXISTS outbox (id TEXT PRIMARY KEY, chat INTEGER NOT NULL, generation INTEGER NOT NULL, body TEXT NOT NULL, kind TEXT NOT NULL, created REAL NOT NULL, next_try REAL NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'pending', message_id INTEGER, keyboard INTEGER NOT NULL DEFAULT 0)");
        db.Run("CREATE TABLE IF NOT EXISTS alerts (id TEXT PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL, local_state TEXT NOT NULL DEFAULT 'pending', created REAL NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS inbox (generation INTEGER NOT NULL, update_id INTEGER NOT NULL, PRIMARY KEY(generation,update_id))");
        db.Run("CREATE TABLE IF NOT EXISTS limit_history (revision INTEGER PRIMARY KEY, changed TEXT NOT NULL, minutes INTEGER NOT NULL, enabled INTEGER NOT NULL, warning INTEGER NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS cloud_dirty_days (day TEXT PRIMARY KEY, revision INTEGER NOT NULL)");
        SetMeta("schema", "1");
    }
    static double Num(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    static double Epoch(DateTimeOffset value) => (value - DateTimeOffset.UnixEpoch).TotalSeconds;
    static DateTimeOffset Time(string value) => DateTimeOffset.UnixEpoch.AddSeconds(Num(value));
    static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string? GetMeta(string key) { lock (gate) return db.Query("SELECT value FROM meta WHERE key=?", key).FirstOrDefault()?[0]; }
    public void SetMeta(string key, string value) { lock (gate) db.Run("INSERT INTO meta(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value); }
    public void DeleteMeta(string key) { lock (gate) db.Run("DELETE FROM meta WHERE key=?", key); }
    public DateTimeOffset? LastObservedEnd()
    {
        lock (gate)
        {
            var value = db.Query("SELECT finish FROM segments WHERE background=0 ORDER BY id DESC LIMIT 1").FirstOrDefault();
            return value is null ? null : Time(value[0]);
        }
    }
    public AppSettings LoadSettings()
    {
        var raw = GetMeta("settings");
        if (raw is null) return new();
        return JsonSerializer.Deserialize<AppSettings>(raw, Wire.Json) ?? throw new IOException("Не удалось прочитать настройки. База сохранена; обратитесь к инструкции восстановления.");
    }
    public void SaveSettings(AppSettings settings)
    {
        lock (gate) Transaction(() =>
        {
            SetMeta("settings", JsonSerializer.Serialize(settings, Wire.Json));
            db.Run("INSERT OR IGNORE INTO limit_history(revision,changed,minutes,enabled,warning) VALUES(?,?,?,?,?)",
                settings.LimitRevision, DateTimeOffset.UtcNow.ToString("O"), settings.LimitMinutes, settings.LimitEnabled, settings.WarningMinutes);
        });
    }
    public void Append(IReadOnlyList<ActivitySlice> slices, TimeZoneInfo zone)
    {
        if (slices.Count == 0) return;
        lock (gate) Transaction(() =>
        {
            foreach (var original in slices)
            {
                if (original.Seconds <= 0) continue;
                foreach (var part in SplitDays(original, zone))
                {
                    var s = part;
                    var start = Epoch(s.Start); var end = Epoch(s.End);
                    var latest = db.Query("SELECT id,finish,kind,app,name,domain,category,start FROM segments WHERE background=? ORDER BY id DESC LIMIT 1", s.Background).FirstOrDefault();
                    // Clock rollback / repeated observations must not create overlapping usage.
                    if (latest is not null && start < Num(latest[1]))
                    {
                        if (end <= Num(latest[1])) continue;
                        start = Num(latest[1]); s = s with { Start = Time(latest[1]) };
                    }
                    bool merge = latest is not null && Math.Abs(Num(latest[1]) - start) < .005 && latest[2] == s.Kind.ToString()
                        && latest[3] == s.AppKey && latest[4] == s.AppName && latest[5] == s.Domain && latest[6] == s.Category
                        && Format.Day(Time(latest[7]), zone) == Format.Day(s.Start, zone);
                    if (merge) db.Run("UPDATE segments SET finish=? WHERE id=?", end, long.Parse(latest![0]));
                    else db.Run("INSERT INTO segments(start,finish,kind,app,name,domain,category,background) VALUES(?,?,?,?,?,?,?,?)",
                        start, end, s.Kind.ToString(), s.AppKey, s.AppName, s.Domain, s.Category, s.Background);
                    db.Run("INSERT INTO daily(day,kind,app,name,domain,category,background,seconds) VALUES(?,?,?,?,?,?,?,?) ON CONFLICT(day,kind,app,name,domain,category,background) DO UPDATE SET seconds=seconds+excluded.seconds",
                        Day(Format.Day(s.Start, zone)), s.Kind.ToString(), s.AppKey, s.AppName, s.Domain, s.Category, s.Background, s.Seconds);
                    MarkCloudDirty(Format.Day(s.Start, zone));
                }
            }
        });
    }
    public static IEnumerable<ActivitySlice> SplitDays(ActivitySlice slice, TimeZoneInfo zone)
    {
        var start = slice.Start;
        while (start < slice.End)
        {
            var next = Format.Midnight(Format.Day(start, zone).AddDays(1), zone);
            if (next <= start) next = start.AddDays(1);
            var end = slice.End < next ? slice.End : next;
            yield return slice with { Start = start, End = end }; start = end;
        }
    }
    public Summary Summarize(DateOnly from, DateOnly to)
    {
        lock (gate)
        {
            var rows = db.Query("SELECT kind,app,name,domain,category,background,SUM(seconds) FROM daily WHERE day>=? AND day<=? GROUP BY kind,app,name,domain,category,background", Day(from), Day(to));
            return BuildSummary(from, to, rows);
        }
    }
    public Summary SummarizeInterval(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var rows = GetSegments(from, to).Select(s => new[] { s.Kind.ToString(), s.AppKey, s.AppName, s.Domain, s.Category, s.Background ? "1" : "0", s.Seconds.ToString("R", CultureInfo.InvariantCulture) }).ToList();
        return BuildSummary(Format.Day(from, zone), Format.Day(to, zone), rows);
    }
    private static Summary BuildSummary(DateOnly from, DateOnly to, List<string[]> rows)
    {
        double Sum(Func<string[], bool> filter) => rows.Where(filter).Sum(r => Num(r[6]));
        var active = rows.Where(r => r[0] == "Active" && r[5] == "0").ToArray();
        var activities = active.GroupBy(r => r[3].Length > 0 ? "site:" + r[3] : "app:" + r[1])
            .Select(g => new UsageRow(g.Key, g.First()[3].Length > 0 ? g.First()[3] : g.First()[2], g.Select(r => r[4]).Distinct().Count() == 1 ? g.First()[4] : "Разные категории", g.Sum(r => Num(r[6]))))
            .OrderByDescending(x => x.Seconds).ToArray();
        var categories = active.GroupBy(r => r[4]).Select(g => new UsageRow(g.Key, g.Key, g.Key, g.Sum(r => Num(r[6])))).OrderByDescending(x => x.Seconds).ToArray();
        return new(from, to, active.Sum(r => Num(r[6])), Sum(r => r[0] == "Idle" && r[5] == "0"),
            Sum(r => r[0] is "Unknown" or "Paused" && r[5] == "0"), Sum(r => r[5] == "1"), activities, categories,
            active.Where(r => Categories.IsBrowser(r[1]) && r[3].Length == 0).Sum(r => Num(r[6])));
    }
    public IReadOnlyList<ActivitySlice> GetSegments(DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate) return db.Query("SELECT start,finish,kind,app,name,domain,category,background FROM segments WHERE finish>? AND start<? ORDER BY start", Epoch(from), Epoch(to))
            .Select(r => new ActivitySlice(Time(r[0]) < from ? from : Time(r[0]), Time(r[1]) > to ? to : Time(r[1]), Enum.Parse<ActivityKind>(r[2]), r[3], r[4], r[5], r[6], r[7] == "1")).ToArray();
    }
    public static double LongestActive(IEnumerable<ActivitySlice> rows)
    {
        double best = 0, run = 0; DateTimeOffset? end = null;
        foreach (var row in rows.Where(r => !r.Background).OrderBy(r => r.Start))
        {
            if (row.Kind != ActivityKind.Active || (end.HasValue && row.Start - end.Value > TimeSpan.FromSeconds(3))) run = 0;
            if (row.Kind == ActivityKind.Active) { run += row.Seconds; best = Math.Max(best, run); }
            end = row.End;
        }
        return best;
    }
    public bool Enqueue(string id, long chat, int generation, string body, string kind, DateTimeOffset now, bool keyboard = false)
    {
        if (chat == 0) return false;
        lock (gate)
        {
            db.Run("INSERT OR IGNORE INTO outbox(id,chat,generation,body,kind,created,next_try,keyboard) VALUES(?,?,?,?,?,?,?,?)", id, chat, generation, body, kind, Epoch(now), Epoch(now), keyboard);
            return db.Changed > 0;
        }
    }
    public bool RecordAlert(LimitEvent alert, AppSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            bool added = false;
            Transaction(() =>
            {
                db.Run("INSERT OR IGNORE INTO alerts(id,title,body,created) VALUES(?,?,?,?)", alert.Id, alert.Title, alert.Body, Epoch(now));
                added = db.Changed > 0;
                if (added && alert.Reached)
                    Enqueue(alert.Id, settings.ParentChatId, settings.TelegramGeneration, $"{alert.Title}\n{alert.Body}\nСобытие: {Format.Stamp(now, settings.Zone)}", "limit", now);
            });
            return added;
        }
    }
    public IReadOnlyList<(string Id, string Title, string Body)> PendingLocalAlerts()
    {
        lock (gate) return db.Query("SELECT id,title,body FROM alerts WHERE local_state='pending' ORDER BY created LIMIT 10").Select(r => (r[0], r[1], r[2])).ToArray();
    }
    public void MarkLocalAttempted(string id) { lock (gate) db.Run("UPDATE alerts SET local_state='attempted' WHERE id=?", id); }
    public Outgoing? NextOutgoing(DateTimeOffset now)
    {
        lock (gate)
        {
            var r = db.Query("SELECT id,chat,generation,body,kind,attempts,created,keyboard FROM outbox WHERE status='pending' AND next_try<=? ORDER BY created LIMIT 1", Epoch(now)).FirstOrDefault();
            return r is null ? null : new(r[0], long.Parse(r[1]), int.Parse(r[2]), r[3], r[4], int.Parse(r[5]), Time(r[6]), r[7] == "1");
        }
    }
    public void MarkSent(string id, long messageId) { lock (gate) db.Run("UPDATE outbox SET status='sent',message_id=? WHERE id=?", messageId, id); }
    public void Retry(string id, DateTimeOffset next, bool permanent) { lock (gate) db.Run("UPDATE outbox SET attempts=attempts+1,next_try=?,status=? WHERE id=?", Epoch(next), permanent ? "error" : "pending", id); }
    public void RetryErrors() { lock (gate) db.Run("UPDATE outbox SET status='pending',next_try=0 WHERE status='error'"); }
    public void CancelPending() { lock (gate) db.Run("UPDATE outbox SET status='cancelled' WHERE status IN ('pending','error')"); }
    public (int Pending, int Errors) QueueCounts()
    {
        lock (gate)
        {
            int Count(string state) => int.Parse(db.Query("SELECT COUNT(*) FROM outbox WHERE status=?", state)[0][0]);
            return (Count("pending"), Count("error"));
        }
    }
    public bool CommandSeen(int generation, long updateId) { lock (gate) return db.Query("SELECT 1 FROM inbox WHERE generation=? AND update_id=?", generation, updateId).Count > 0; }
    public long Offset(int generation) => long.TryParse(GetMeta("offset:" + generation), out var x) ? x : 0;
    public void FinishCommand(int generation, long updateId, Action action)
    {
        lock (gate) Transaction(() =>
        {
            if (!CommandSeen(generation, updateId))
            {
                action(); db.Run("INSERT INTO inbox(generation,update_id) VALUES(?,?)", generation, updateId);
            }
            SetMeta("offset:" + generation, Math.Max(Offset(generation), updateId + 1).ToString(CultureInfo.InvariantCulture));
        });
    }
    public void Prune(AppSettings c, DateTimeOffset now)
    {
        lock (gate) Transaction(() =>
        {
            db.Run("DELETE FROM segments WHERE finish<?", Epoch(now.AddDays(-c.HistoryDays)));
            db.Run("DELETE FROM daily WHERE day<?", Day(Format.Day(now, c.Zone).AddDays(-c.SummaryDays)));
            db.Run("DELETE FROM cloud_dirty_days WHERE day<?", Day(Format.Day(now, c.Zone).AddDays(-c.SummaryDays)));
            db.Run("DELETE FROM alerts WHERE created<?", Epoch(now.AddDays(-c.SummaryDays)));
            db.Run("DELETE FROM outbox WHERE status IN ('sent','cancelled') AND created<?", Epoch(now.AddDays(-30)));
        });
    }
    public void ClearHistory()
    {
        lock (gate) Transaction(() =>
        {
            db.Run("DELETE FROM segments"); db.Run("DELETE FROM daily"); db.Run("DELETE FROM alerts");
            db.Run("DELETE FROM outbox");
            db.Run("DELETE FROM cloud_dirty_days");
            MarkCloudDirty(Format.Day(DateTimeOffset.UtcNow, LoadSettings().Zone));
            db.Run("DELETE FROM meta WHERE key IN ('session-start','session-last','daily-cursor','last-observation')");
        });
    }
    private void Transaction(Action action)
    {
        db.Run("BEGIN IMMEDIATE");
        try { action(); db.Run("COMMIT"); }
        catch { try { db.Run("ROLLBACK"); } catch { } throw; }
    }
    public void Dispose() { lock (gate) db.Dispose(); }
}
