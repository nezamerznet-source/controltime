using System.Text.Json;
using System.Net;
using System.Net.Http;
using FamilyTime.Core;

var cases = new List<(string Name, Action Run)>();
var epoch = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
var today = DateOnly.FromDateTime(epoch.UtcDateTime);
var config = new AppSettings { SetupCompleted = true, TimeZoneId = "UTC", ParentChatId = 101, ParentUserId = 101, TelegramGeneration = 1 };
Observation Obs(double seconds, string app = "game", string domain = "", bool video = false, bool background = false,
    double idleAge = 0, bool locked = false, bool paused = false) =>
    new(epoch.AddSeconds(seconds), seconds, epoch.AddSeconds(seconds - idleAge), locked, paused, app, app, domain,
        domain == "youtube.com" ? "Видео" : "Игры", video, background);
ActivitySlice Slice(double start, double end, ActivityKind kind = ActivityKind.Active, bool bg = false,
    string app = "game", string domain = "", string category = "Игры") => new(epoch.AddSeconds(start), epoch.AddSeconds(end), kind, app, app, domain, category, bg);
Summary Summary(double seconds) => new(today, today, seconds, 0, 0, 0, [], []);
void Test(string name, Action body) => cases.Add((name, body));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Near(double expected, double actual) { if (Math.Abs(expected - actual) > .01) throw new Exception($"Expected {expected}, got {actual}"); }
void True(bool value) { if (!value) throw new Exception("Expected true"); }
var idleThreshold = TimeSpan.FromMinutes(5);

Test("parent password is salted, unicode-safe and rejects wrong input", () =>
{
    const string password = "Пароль-родителя-123";
    var first = ParentPassword.Create(password); var second = ParentPassword.Create(password);
    True(first != second); True(!first.Contains(password));
    True(ParentPassword.Verify(password, first)); True(!ParentPassword.Verify("wrong", first));
    True(!ParentPassword.Verify(password, "")); True(!ParentPassword.Verify(password, "damaged"));
    True(!ParentPassword.Verify(password, first.Replace("600000", "999999999")));
    True(!ParentPassword.Verify(new string('x', 129), first));
    bool rejected = false; try { ParentPassword.Create("short"); } catch (ArgumentException) { rejected = true; }
    True(rejected);
});
Test("legacy settings load without password and new verifier survives save", () =>
{
    var legacy = JsonSerializer.Deserialize<AppSettings>("{\"setupCompleted\":true}", Wire.Json)!;
    Equal("", legacy.ParentPasswordHash);
    using var store = new ActivityStore(":memory:");
    var hash = ParentPassword.Create("parent-12345");
    store.SaveSettings(config with { ParentPasswordHash = hash });
    True(ParentPassword.Verify("parent-12345", store.LoadSettings().ParentPasswordHash));
    store.ClearHistory(); Equal(hash, store.LoadSettings().ParentPasswordHash);
});

Test("foreground switches partition elapsed time without browser double count", () =>
{
    var engine = new AccountingEngine(); using var store = new ActivityStore(":memory:");
    engine.Observe(Obs(0), idleThreshold);
    store.Append(engine.Observe(Obs(2, "chrome", "youtube.com", true), idleThreshold), TimeZoneInfo.Utc);
    store.Append(engine.Observe(Obs(5, "chrome", "youtube.com", true), idleThreshold), TimeZoneInfo.Utc);
    var sum = store.Summarize(today, today); Near(5, sum.ActiveSeconds); Equal(2, sum.Activities.Count);
    Near(2, sum.Activities.Single(a => a.Key == "app:game").Seconds);
    Near(3, sum.Activities.Single(a => a.Key == "site:youtube.com").Seconds);
});
Test("idle threshold splits the interval at its exact boundary", () =>
{
    var engine = new AccountingEngine(); engine.Observe(Obs(0, idleAge: 299), idleThreshold);
    var slices = engine.Observe(Obs(3, idleAge: 302), idleThreshold);
    Near(1, slices.Single(s => s.Kind == ActivityKind.Active).Seconds);
    Near(2, slices.Single(s => s.Kind == ActivityKind.Idle).Seconds);
});
Test("foreground playback extends active time until paused", () =>
{
    var e = new AccountingEngine(); e.Observe(Obs(0, "chrome", "youtube.com", true, idleAge: 900), idleThreshold);
    var first = e.Observe(Obs(2, "chrome", "youtube.com", false, idleAge: 902), idleThreshold);
    Near(2, first.Single(s => s.Kind == ActivityKind.Active).Seconds);
    var second = e.Observe(Obs(4, "chrome", "youtube.com", false, idleAge: 904), idleThreshold);
    True(second.All(s => s.Kind == ActivityKind.Idle));
});
Test("background playback is separate and does not spend the budget twice", () =>
{
    var e = new AccountingEngine(); using var store = new ActivityStore(":memory:");
    e.Observe(Obs(0, background: true), idleThreshold);
    store.Append(e.Observe(Obs(10, background: true), idleThreshold), TimeZoneInfo.Utc);
    var s = store.Summarize(today, today); Near(10, s.ActiveSeconds); Near(10, s.BackgroundSeconds); Near(7190, s.Remaining(config));
});
Test("lock followed by long sleep never creates active time", () =>
{
    var e = new AccountingEngine(); e.Observe(Obs(0, locked: true), idleThreshold);
    var s = e.Observe(Obs(18000), idleThreshold); Equal(ActivityKind.Locked, s.Single().Kind); Near(18000, s.Single().Seconds);
});
Test("unobserved long interval is unknown, not active", () =>
{
    var e = new AccountingEngine(); e.Observe(Obs(0), idleThreshold);
    var s = e.Observe(Obs(120), idleThreshold); Equal(ActivityKind.Unknown, s.Single().Kind);
});
Test("paused intervals do not consume a daily limit", () =>
{
    var e = new AccountingEngine(); using var store = new ActivityStore(":memory:");
    e.Observe(Obs(0, paused: true), idleThreshold); store.Append(e.Observe(Obs(30), idleThreshold), TimeZoneInfo.Utc);
    var s = store.Summarize(today, today); Near(0, s.ActiveSeconds); Near(30, s.UnknownSeconds);
});
Test("clock rollback and replay do not duplicate persisted time", () =>
{
    using var store = new ActivityStore(":memory:");
    store.Append([Slice(0, 60)], TimeZoneInfo.Utc);
    store.Append([Slice(0, 60), Slice(30, 90)], TimeZoneInfo.Utc);
    Near(90, store.Summarize(today, today).ActiveSeconds);
    Equal(1, store.GetSegments(epoch, epoch.AddMinutes(2)).Count);
    var engine = new AccountingEngine(); engine.Observe(Obs(10), idleThreshold);
    Equal(0, engine.Observe(Obs(5), idleThreshold).Count);
});
Test("local midnight splits a single interval across two days", () =>
{
    using var store = new ActivityStore(":memory:"); var start = new DateTimeOffset(2026, 9, 28, 23, 59, 57, TimeSpan.Zero);
    store.Append([new(start, start.AddSeconds(6), ActivityKind.Active, "game", "Game", "", "Игры")], TimeZoneInfo.Utc);
    Near(3, store.Summarize(today, today).ActiveSeconds); Near(3, store.Summarize(today.AddDays(1), today.AddDays(1)).ActiveSeconds);
});
foreach (var (day, expected) in new[] { (new DateOnly(2026, 3, 8), 23d), (new DateOnly(2026, 11, 1), 25d) })
    Test($"DST day {day} has {expected} elapsed hours", () =>
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var from = Format.Midnight(day, zone); var to = Format.Midnight(day.AddDays(1), zone);
        using var store = new ActivityStore(":memory:");
        store.Append([new(from, to, ActivityKind.Active, "game", "Game", "", "Игры")], zone);
        Near(expected * 3600, store.Summarize(day, day).ActiveSeconds);
    });
Test("database restart retains usage, one threshold event and its outbox entry", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "familytime-test-" + Guid.NewGuid() + ".db");
    try
    {
        var alert = LimitPolicy.Evaluate(Summary(7200), config)!;
        using (var s = new ActivityStore(path))
        {
            s.SaveSettings(config); s.Append([Slice(0, 7200)], TimeZoneInfo.Utc);
            True(s.RecordAlert(alert, config, epoch)); s.MarkLocalAttempted(alert.Id);
        }
        using (var s = new ActivityStore(path))
        {
            Near(7200, s.Summarize(today, today).ActiveSeconds); Equal(config.LimitMinutes, s.LoadSettings().LimitMinutes);
            Equal(epoch.AddHours(2), s.LastObservedEnd()!.Value);
            True(!s.RecordAlert(alert, config, epoch.AddMinutes(1))); Equal(0, s.PendingLocalAlerts().Count);
            Equal(1, s.QueueCounts().Pending); Equal(alert.Id, s.NextOutgoing(epoch.AddMinutes(2))!.Id);
        }
    }
    finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
});
Test("limit reaches at equality, warning is skipped when already exceeded", () =>
{
    Equal(false, LimitPolicy.Evaluate(Summary(7199), config)!.Reached);
    Equal(true, LimitPolicy.Evaluate(Summary(7200), config)!.Reached);
    True(LimitPolicy.Evaluate(Summary(9000), config)!.Id.EndsWith(":reached"));
    True(LimitPolicy.Evaluate(Summary(6000), config) is null);
    True(LimitPolicy.Evaluate(Summary(7100), config with { WarningMinutes = 0 }) is null);
    True(LimitPolicy.Evaluate(Summary(9000), config with { LimitEnabled = false }) is null);
});
Test("new day and changed limit revision have independent threshold events", () =>
{
    var a = LimitPolicy.Evaluate(Summary(7200), config)!;
    var b = LimitPolicy.Evaluate(Summary(7200), config with { LimitRevision = 2, LimitMinutes = 100 })!;
    var c = LimitPolicy.Evaluate(Summary(7200) with { From = today.AddDays(1), To = today.AddDays(1) }, config)!;
    True(a.Id != b.Id && a.Id != c.Id);
    using var store = new ActivityStore(":memory:");
    True(store.RecordAlert(a, config, epoch)); True(!store.RecordAlert(a, config, epoch)); True(store.RecordAlert(b, config, epoch));
});
Test("summary categories reconcile and missing browser detail is visible", () =>
{
    using var s = new ActivityStore(":memory:");
    s.Append([Slice(0, 60), Slice(60, 90, app: "chrome", category: "Другое"), Slice(90, 110, ActivityKind.Idle), Slice(110, 120, ActivityKind.Unknown)], TimeZoneInfo.Utc);
    var result = s.Summarize(today, today);
    Near(90, result.ActiveSeconds); Near(result.ActiveSeconds, result.Activities.Sum(r => r.Seconds));
    Near(result.ActiveSeconds, result.Categories.Sum(r => r.Seconds)); Near(30, result.BrowserUnknownSeconds);
    Near(20, result.IdleSeconds); Near(10, result.UnknownSeconds);
});
Test("longest continuous use spans app changes, but not idle breaks", () =>
{
    Near(90, ActivityStore.LongestActive([Slice(0, 60), Slice(60, 90, app: "chrome"), Slice(90, 120, ActivityKind.Idle), Slice(120, 180)]));
});
Test("browser state expires, stale sequence cannot overwrite, ambiguous focus is unknown", () =>
{
    var r = new BrowserRegistry(); string id = Guid.NewGuid().ToString();
    var state = new BrowserState { Browser = "chrome", InstanceId = id, Sequence = 2, Focused = true, Domain = "youtube.com", ForegroundPlaying = true };
    r.Accept(state, epoch); r.Accept(state with { Sequence = 1, Domain = "wrong.example" }, epoch);
    Equal("youtube.com", r.Resolve("chrome", epoch).Domain); True(r.Resolve("game", epoch).Background);
    True(!r.Resolve("game", epoch).Foreground); True(!r.Resolve("chrome", epoch.AddSeconds(8)).Connected);
    r.Accept(state with { InstanceId = Guid.NewGuid().ToString() }, epoch);
    Equal("", r.Resolve("chrome", epoch).Domain);
});
Test("untrusted browser domain cannot persist a URL or spoof non-YouTube playback", () =>
{
    Equal("", Categories.DomainOnly("https://site.test/path?secret=1")); Equal("site.test", Categories.DomainOnly("www.site.test"));
    var r = new BrowserRegistry();
    r.Accept(new() { Browser = "chrome", InstanceId = Guid.NewGuid().ToString(), Sequence = 1, Focused = true, Domain = "school.test", ForegroundPlaying = true }, epoch);
    Equal("school.test", r.Resolve("chrome", epoch).Domain); True(!r.Resolve("chrome", epoch).Foreground);
    Equal("Учёба", Categories.Resolve("chrome", "school.test", config with { CategoryRules = new() { ["site:school.test"] = "Учёба" } }));
});
JsonElement Update(long id, long chat = 101, long user = 101, string text = "/today", string type = "private", int age = 0) =>
    JsonSerializer.SerializeToElement(new { update_id = id, message = new { chat = new { id = chat, type }, from = new { id = user }, text, date = epoch.AddSeconds(-age).ToUnixTimeSeconds() } });
Test("Telegram rejects another user, another chat and groups", () =>
{
    using var s = new ActivityStore(":memory:"); int reads = 0;
    var w = new TelegramWorker(s, new FakeApi(), () => config, () => "fake", _ => { reads++; return "report"; }, (_, _, _) => false);
    w.Handle(Update(1, user: 202), 1, epoch); w.Handle(Update(2, chat: 202), 1, epoch); w.Handle(Update(3, type: "group"), 1, epoch);
    Equal(0, reads); Equal(0, s.QueueCounts().Pending); Equal(4L, s.Offset(1));
});
Test("Telegram repeated update is idempotent and old update cannot regress offset", () =>
{
    using var s = new ActivityStore(":memory:"); int reads = 0;
    var w = new TelegramWorker(s, new FakeApi(), () => config, () => "fake", _ => { reads++; return "report"; }, (_, _, _) => false);
    w.Handle(Update(10), 1, epoch); w.Handle(Update(10), 1, epoch.AddSeconds(5));
    w.Handle(Update(9, user: 999), 1, epoch.AddSeconds(6));
    Equal(1, reads); Equal(1, s.QueueCounts().Pending); Equal(11L, s.Offset(1));
});
Test("Telegram remaining command requests the same current snapshot and labels delayed request", () =>
{
    using var s = new ActivityStore(":memory:"); bool remaining = false;
    var w = new TelegramWorker(s, new FakeApi(), () => config, () => "fake", r => { remaining = r; return "current snapshot"; }, (_, _, _) => false);
    w.Handle(Update(1, text: "Сколько осталось", age: 600), 1, epoch);
    True(remaining); True(s.NextOutgoing(epoch)!.Body.Contains("после задержки")); True(s.NextOutgoing(epoch)!.Body.Contains("current snapshot"));
});
Test("pairing code requires match, expires, and stores both numeric identities", () =>
{
    using var s = new ActivityStore(":memory:"); var code = Pairing.CreateCode();
    var c = config with { ParentChatId = 0, ParentUserId = 0, PairingHash = Pairing.Hash(code), PairingExpires = epoch.AddMinutes(10) };
    var w = new TelegramWorker(s, new FakeApi(), () => c, () => "fake", _ => "secret", (chat, user, entered) =>
    {
        if (!Pairing.Matches(entered, c.PairingHash)) return false;
        c = c with { ParentChatId = chat, ParentUserId = user, PairingHash = "" }; return true;
    });
    w.Handle(Update(1, text: "/start " + new string('0', 24)), 1, epoch); Equal(0L, c.ParentChatId);
    w.Handle(Update(2, text: "/start " + code), 1, epoch.AddMinutes(11)); Equal(0L, c.ParentChatId);
    w.Handle(Update(3, chat: 404, user: 404, text: "/start " + code), 1, epoch);
    Equal(404L, c.ParentChatId); Equal(404L, c.ParentUserId); Equal(1, s.QueueCounts().Pending);
});
Test("durable outbox handles backoff, permanent errors, retry and deduplication", () =>
{
    using var s = new ActivityStore(":memory:");
    True(s.Enqueue("x", 101, 1, "body", "test", epoch)); True(!s.Enqueue("x", 101, 1, "body", "test", epoch));
    s.Retry("x", epoch.AddMinutes(1), false); True(s.NextOutgoing(epoch) is null);
    Equal(1, s.NextOutgoing(epoch.AddMinutes(1))!.Attempts);
    s.Retry("x", epoch, true); Equal(1, s.QueueCounts().Errors); s.RetryErrors(); Equal(1, s.QueueCounts().Pending);
    s.MarkSent("x", 900); True(s.NextOutgoing(epoch) is null); True(!s.Enqueue("x", 101, 1, "body", "test", epoch));
});
Test("failed command transaction rolls back outbox and inbox together", () =>
{
    using var s = new ActivityStore(":memory:");
    try { s.FinishCommand(1, 1, () => { s.Enqueue("request", 101, 1, "body", "test", epoch); throw new InvalidOperationException(); }); }
    catch (InvalidOperationException) { }
    Equal(0, s.QueueCounts().Pending); True(!s.CommandSeen(1, 1)); Equal(0L, s.Offset(1));
});
Test("retention removes detailed history without losing retained daily totals", () =>
{
    using var s = new ActivityStore(":memory:"); s.Append([Slice(0, 60)], TimeZoneInfo.Utc);
    s.Prune(config with { HistoryDays = 7, SummaryDays = 365 }, epoch.AddDays(10));
    Equal(0, s.GetSegments(epoch, epoch.AddMinutes(1)).Count); Near(60, s.Summarize(today, today).ActiveSeconds);
});
Test("clear history resets usage and queued reports but preserves settings and replay protection", () =>
{
    using var s = new ActivityStore(":memory:"); s.SaveSettings(config); s.Append([Slice(0, 60)], TimeZoneInfo.Utc);
    s.FinishCommand(1, 1, () => s.Enqueue("request", 101, 1, "body", "test", epoch)); s.ClearHistory();
    Near(0, s.Summarize(today, today).ActiveSeconds); Equal(0, s.QueueCounts().Pending);
    Equal(101L, s.LoadSettings().ParentUserId); True(s.CommandSeen(1, 1));
});
Test("historical and session reports never present subtotal as today's remaining budget", () =>
{
    var old = Summary(120) with { From = today.AddDays(-1), To = today.AddDays(-1) };
    True(!Reports.Render(old, config, epoch, "история").Contains("Осталось:"));
    True(!Reports.Render(Summary(120), config, epoch, "сеанс", includeLimit: false).Contains("Осталось:"));
    True(Reports.Render(Summary(120), config, epoch, "сегодня").Contains("Осталось: 1 ч 58 мин"));
});

Test("cloud queue coalesces observations and late acknowledgements preserve new accounting", () =>
{
    using var s = new ActivityStore(":memory:");
    s.Append([Slice(0, 60)], TimeZoneInfo.Utc);
    var first = s.NextCloudDay(config, epoch)!; Near(60, first.ActiveSeconds);
    s.Append([Slice(60, 90)], TimeZoneInfo.Utc);
    s.AcknowledgeCloud(new(first.Day, first.Revision)); Equal(1, s.CloudPendingDays());
    var next = s.NextCloudDay(config, epoch)!; True(next.Revision > first.Revision); Near(90, next.ActiveSeconds);
    s.AcknowledgeCloud(new(next.Day, next.Revision)); Equal(0, s.CloudPendingDays());
});
Test("cloud session activity excludes idle and background without losing app totals", () =>
{
    var sessions = ActivityStore.CloudSessions([Slice(0,60), Slice(60,120, ActivityKind.Idle), Slice(0,120,bg:true), Slice(120,180,app:"chrome",domain:"youtube.com"), Slice(1200,1260)], 15);
    Equal(2, sessions.Count); Near(120, sessions[0].ActiveSeconds); Near(120, sessions[0].Activities.Sum(a => a.Seconds));
    Equal(epoch, sessions[0].Start); Equal(epoch.AddSeconds(180), sessions[0].End);
});
Test("cloud backlog persists and old days cannot be starved by today's observations", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "familytime-cloud-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        using (var s = new ActivityStore(path)) { s.Append([Slice(0,60)], TimeZoneInfo.Utc); s.QueueCloudHistory(today.AddDays(1)); }
        using var reopened = new ActivityStore(path);
        Equal(2, reopened.CloudPendingDays());
        Equal(today.AddDays(1).ToString("yyyy-MM-dd"), reopened.NextCloudDay(config, epoch.AddDays(1))!.Day);
        Equal(today.ToString("yyyy-MM-dd"), reopened.NextCloudDay(config, epoch.AddDays(1), false)!.Day);
        True(reopened.NextCloudDay(config, epoch.AddDays(100))!.Sessions is null);
    }
    finally { File.Delete(path); }
});
Test("clearing local history keeps cloud credentials and sends a newer zero snapshot", () =>
{
    using var s = new ActivityStore(":memory:"); var now = DateTimeOffset.UtcNow;
    s.SaveSettings(config with { CloudLinked = true, CloudDeviceId = "device", ProtectedCloudToken = "encrypted" });
    s.QueueCloudHistory(Format.Day(now, TimeZoneInfo.Utc)); var before = s.NextCloudDay(config, now)!;
    s.ClearHistory(); var after = s.NextCloudDay(config, now)!;
    True(after.Revision > before.Revision); Near(0, after.ActiveSeconds); Equal("encrypted", s.LoadSettings().ProtectedCloudToken);
});
Test("cloud endpoint rejects insecure, local and non-root destinations", () =>
{
    Equal("https://family.example.com", CloudEndpoint.Normalize("https://family.example.com/"));
    foreach (var value in new[] { "http://example.com", "https://127.0.0.1", "https://localhost", "https://user:pass@example.com", "https://example.com/path", "https://example.com?token=x" })
    { bool rejected = false; try { CloudEndpoint.Normalize(value); } catch (ArgumentException) { rejected = true; } True(rejected); }
});

Test("cloud HTTP protocol confirms exact revisions and reports revoked and offline errors", () =>
{
    using var store = new ActivityStore(":memory:"); store.Append([Slice(0,60)], TimeZoneInfo.Utc);
    var day = store.NextCloudDay(config, epoch)!;
    var meta = new CloudMetadata("Мирон", "UTC", 120, true, false, epoch, false, 90, 365);
    using var good = new HttpClient(new CloudHandler(async request =>
    {
        Equal("https://family.example.com/api/device/sync", request.RequestUri!.ToString());
        Equal("Bearer", request.Headers.Authorization!.Scheme); Equal(new string('x',43), request.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Equal(1, body.RootElement.GetProperty("protocol").GetInt32()); Equal(1, body.RootElement.GetProperty("days").GetArrayLength());
        True(!body.RootElement.GetProperty("metadata").TryGetProperty("protectedToken", out _));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new CloudSyncResponse([new(day.Day,day.Revision)]), Wire.Json)) };
    }));
    var result = new CloudClient(good).Sync("https://family.example.com", new string('x',43), meta, day, CancellationToken.None).GetAwaiter().GetResult();
    Equal(day.Revision, result.Accepted.Single().Revision);
    using var revoked = new HttpClient(new CloudHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
    bool rejected = false;
    try { new CloudClient(revoked).Sync("https://family.example.com", new string('x',43), meta, day, CancellationToken.None).GetAwaiter().GetResult(); }
    catch (CloudError ex) { rejected = ex.Revoked; } True(rejected); Equal(1, store.CloudPendingDays());
    using var offline = new HttpClient(new CloudHandler(_ => throw new HttpRequestException("offline")));
    bool queued = false;
    try { new CloudClient(offline).Sync("https://family.example.com", new string('x',43), meta, day, CancellationToken.None).GetAwaiter().GetResult(); }
    catch (CloudError ex) { queued = !ex.Revoked; } True(queued); Equal(1, store.CloudPendingDays());
});

int failed = 0;
foreach (var item in cases)
{
    try { item.Run(); Console.WriteLine("PASS " + item.Name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + item.Name + "\n" + e); }
}
Console.WriteLine($"{cases.Count - failed}/{cases.Count} checks passed");
return failed == 0 ? 0 : 1;

sealed class FakeApi : ITelegramApi
{
    public Task<JsonElement[]> Poll(string token, long offset, CancellationToken ct) => Task.FromResult(Array.Empty<JsonElement>());
    public Task<long> Send(string token, long chat, string body, bool keyboard, CancellationToken ct) => Task.FromResult(1L);
    public Task<string> Username(string token, CancellationToken ct) => Task.FromResult("test_bot");
}

sealed class CloudHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
}
