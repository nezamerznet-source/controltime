using System.Globalization;
using System.Net.Http;

namespace FamilyTime.Windows;

public sealed class AppRuntime : IDisposable
{
    public readonly ActivityStore Store;
    public readonly BrowserRegistry Browsers = new();
    private readonly object gate = new();
    private readonly AccountingEngine engine = new();
    private readonly WindowsProbe probe = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly HttpClient http = new();
    private Task? tasks;
    private DateTimeOffset lastSchedule, lastPrune;
    private string encryptedCache = "", tokenCache = "";
    private AppSettings config;
    private readonly bool integrateWindows;
    public ParentAccess Parent { get; }
    public AppSettings Settings { get { lock (gate) return config; } }
    public bool Paused { get; private set; }
    public string State { get; private set; } = "Настройка";
    public DateTimeOffset? LastObservation { get; private set; }
    public string? Error { get; private set; }
    public TelegramWorker Telegram { get; }
    public event Action<string, string>? Notify;

    public AppRuntime(string? dataDirectory = null, bool integrateWindows = true)
    {
        this.integrateWindows = integrateWindows;
        Store = new ActivityStore(Path.Combine(dataDirectory ?? WindowsIntegration.DataDirectory, "activity.db"));
        config = Store.LoadSettings(); Paused = Store.GetMeta("paused") == "1";
        Parent = new ParentAccess(Store, () => Settings);
        var now = DateTimeOffset.UtcNow; var previous = Store.LastObservedEnd();
        if (config.SetupCompleted && previous.HasValue && now - previous.Value > TimeSpan.FromSeconds(2))
        {
            var start = previous.Value < now.AddDays(-config.SummaryDays) ? now.AddDays(-config.SummaryDays) : previous.Value;
            Store.Append([new(start, now, ActivityKind.Unknown, "", "Учёт не работал", "", "Другое")], config.Zone);
        }
        Telegram = new TelegramWorker(Store, new TelegramApi(http), () => Settings, Token, CurrentReport, Bind);
        probe.BoundaryChanged += Boundary;
    }
    public void Start()
    {
        tasks = Task.WhenAll(Task.Run(Loop), Task.Run(() => new BrowserPipe(Browsers).Run(cancellation.Token)), Task.Run(() => Telegram.Run(cancellation.Token)));
    }
    private void Boundary() { try { Tick(); } catch { Error = "Не удалось сохранить изменение состояния Windows."; } }
    public void Tick()
    {
        lock (gate)
        {
            if (!config.SetupCompleted) { engine.Reset(); State = "Требуется первый запуск"; return; }
            var sample = probe.Read(config, Paused, Browsers);
            var slices = engine.Observe(sample, TimeSpan.FromMinutes(config.IdleMinutes));
            Store.Append(slices, config.Zone);
            LastObservation = sample.At;
            State = Paused ? "Учёт приостановлен" : sample.Locked ? "Экран заблокирован / сон" :
                sample.ForegroundMedia ? "Видео на переднем плане" : sample.At - sample.LastInput >= TimeSpan.FromMinutes(config.IdleMinutes) ? "Предполагаемый простой" : "Учёт работает";
            foreach (var slice in slices.Where(s => s.Kind == ActivityKind.Active && !s.Background))
            {
                if (Store.GetMeta("session-start") is null) Store.SetMeta("session-start", slice.Start.ToString("O"));
                Store.SetMeta("session-last", slice.End.ToString("O"));
            }
            var today = Store.Summarize(Format.Day(sample.At, config.Zone), Format.Day(sample.At, config.Zone));
            var limit = LimitPolicy.Evaluate(today, config);
            if (limit is not null) Store.RecordAlert(limit, config, sample.At);
            foreach (var local in Store.PendingLocalAlerts())
            {
                Store.MarkLocalAttempted(local.Id);
                Notify?.Invoke(local.Title, local.Body);
            }
            if (sample.At - lastSchedule >= TimeSpan.FromSeconds(10)) { Schedule(sample.At); lastSchedule = sample.At; }
            if (sample.At - lastPrune >= TimeSpan.FromHours(6)) { Store.Prune(config, sample.At); lastPrune = sample.At; }
            Error = null;
        }
    }
    async Task Loop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                try { Tick(); } catch { Error = "Учёт не сохранён. Проверьте свободное место и доступ к папке данных."; engine.Reset(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void TogglePause()
    {
        lock (gate) { Tick(); Paused = !Paused; Store.SetMeta("paused", Paused ? "1" : "0"); Tick(); }
    }
    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        lock (gate)
        {
            Tick(); var next = change(config);
            var validation = next.Validate(); if (validation is not null) throw new ArgumentException(validation);
            if (next.TimeZoneId != config.TimeZoneId) throw new ArgumentException("В этой версии часовой пояс фиксируется при первом запуске.");
            if (next.LimitMinutes != config.LimitMinutes || next.LimitEnabled != config.LimitEnabled || next.WarningMinutes != config.WarningMinutes)
                next = next with { LimitRevision = config.LimitRevision + 1 };
            bool changedLimit = next.LimitRevision != config.LimitRevision;
            Store.SaveSettings(next); config = next;
            if (integrateWindows) WindowsIntegration.AutoStart(config.AutoStart);
            if (changedLimit)
            {
                var now = DateTimeOffset.UtcNow; var today = Format.Day(now, config.Zone);
                var alert = LimitPolicy.Evaluate(Store.Summarize(today, today), config);
                if (alert is not null && alert.Reached) Store.RecordAlert(alert with { Body = alert.Body + "\nУведомление после изменения дневного лимита." }, config, now);
            }
            Store.RetryErrors(); Tick();
        }
    }
    string Token()
    {
        lock (gate)
        {
            if (encryptedCache == config.ProtectedToken) return tokenCache;
            try { tokenCache = SecretStore.Unprotect(config.ProtectedToken); encryptedCache = config.ProtectedToken; }
            catch { tokenCache = ""; Error = "Токен недоступен в этой учётной записи. Подключите Telegram заново."; }
            return tokenCache;
        }
    }
    public async Task<(string Username, string Code)> ConnectTelegram(string rawToken)
    {
        rawToken = rawToken.Trim();
        var username = await new TelegramApi(http).Username(rawToken, cancellation.Token);
        var code = Pairing.CreateCode(); var protectedToken = SecretStore.Protect(rawToken);
        lock (gate)
        {
            Store.CancelPending();
            config = config with { ProtectedToken = protectedToken, BotUsername = username, ParentChatId = 0, ParentUserId = 0,
                TelegramGeneration = config.TelegramGeneration + 1, PairingHash = Pairing.Hash(code), PairingExpires = DateTimeOffset.UtcNow.AddMinutes(10) };
            Store.SaveSettings(config);
        }
        return (username, code);
    }
    bool Bind(long chat, long user, string code)
    {
        lock (gate)
        {
            if (config.ParentChatId != 0 || DateTimeOffset.UtcNow > config.PairingExpires || !Pairing.Matches(code, config.PairingHash)) return false;
            config = config with { ParentChatId = chat, ParentUserId = user, PairingHash = "", PairingExpires = DateTimeOffset.MinValue };
            Store.SaveSettings(config); return true;
        }
    }
    public void DisconnectTelegram()
    {
        lock (gate)
        {
            Store.CancelPending();
            config = config with { ProtectedToken = "", ParentChatId = 0, ParentUserId = 0, BotUsername = "", PairingHash = "", TelegramGeneration = config.TelegramGeneration + 1 };
            Store.SaveSettings(config); encryptedCache = ""; tokenCache = "";
        }
    }
    public string CurrentReport(bool remainingOnly)
    {
        lock (gate)
        {
            Tick(); var now = DateTimeOffset.UtcNow; var today = Format.Day(now, config.Zone);
            var summary = Store.Summarize(today, today);
            double longest = ActivityStore.LongestActive(Store.GetSegments(Format.Midnight(today, config.Zone), now));
            return Reports.Render(summary, config, now, "сегодня на текущий момент", remainingOnly, State, longest);
        }
    }
    public void SendCurrentReport()
    {
        var text = CurrentReport(false); var c = Settings;
        if (c.ParentChatId == 0) throw new InvalidOperationException("Сначала привяжите Telegram в настройках.");
        Store.Enqueue("manual:" + Guid.NewGuid().ToString("N"), c.ParentChatId, c.TelegramGeneration, text, "manual", DateTimeOffset.UtcNow, true);
    }
    void Schedule(DateTimeOffset now)
    {
        var day = Format.Day(now, config.Zone);
        var startRaw = Store.GetMeta("session-start"); var lastRaw = Store.GetMeta("session-last");
        if (DateTimeOffset.TryParse(startRaw, out var start) && DateTimeOffset.TryParse(lastRaw, out var last) && now - last >= TimeSpan.FromMinutes(config.SessionMinutes))
        {
            if (config.SessionReport && config.ParentChatId != 0)
            {
                var summary = Store.SummarizeInterval(start, last, config.Zone);
                var title = $"сеанс {Format.Stamp(start, config.Zone)} — {Format.Stamp(last, config.Zone)}";
                var body = Reports.Render(summary, config, now, title, includeLimit: false)
                    + "\nЗа текущий день: " + Format.Duration(Store.Summarize(day, day).ActiveSeconds);
                Queue("session:" + start.ToUnixTimeMilliseconds(), body, "session", now);
            }
            Store.DeleteMeta("session-start"); Store.DeleteMeta("session-last");
        }
        if (config.ParentChatId == 0) return;
        if (config.DailyReport)
        {
            var yesterday = day.AddDays(-1);
            var cursorRaw = Store.GetMeta("daily-cursor");
            var cursor = DateOnly.TryParse(cursorRaw, out var d) ? d : yesterday.AddDays(-1);
            if (cursor < yesterday)
            {
                var from = cursor.AddDays(1);
                if (from < day.AddDays(-config.SummaryDays)) from = day.AddDays(-config.SummaryDays);
                var sum = Store.Summarize(from, yesterday);
                if (sum.ActiveSeconds > 0) Queue("daily:" + yesterday.ToString("yyyy-MM-dd"), Reports.Render(sum, config, now, from == yesterday ? "итог завершённого дня" : "итоги за пропущенные дни"), "daily", now);
                Store.SetMeta("daily-cursor", yesterday.ToString("yyyy-MM-dd"));
            }
        }
        else Store.SetMeta("daily-cursor", day.AddDays(-1).ToString("yyyy-MM-dd"));
        if (config.EveningReport && TimeZoneInfo.ConvertTime(now, config.Zone).Hour >= config.EveningHour)
        {
            var sum = Store.Summarize(day, day);
            if (sum.ActiveSeconds > 0) Queue("evening:" + day.ToString("yyyy-MM-dd"), Reports.Render(sum, config, now, "вечерний срез; день ещё не завершён"), "evening", now);
        }
        if (config.WeeklyReport)
        {
            int daysSinceMonday = ((int)day.DayOfWeek + 6) % 7;
            var end = day.AddDays(-daysSinceMonday - 1); var first = end.AddDays(-6);
            var sum = Store.Summarize(first, end);
            if (sum.ActiveSeconds > 0) Queue("week:" + first.ToString("yyyy-MM-dd"), Reports.Render(sum, config, now, "итог завершённой недели"), "week", now);
        }
    }
    void Queue(string id, string body, string kind, DateTimeOffset now) => Store.Enqueue(id, config.ParentChatId, config.TelegramGeneration, body, kind, now);
    public void ClearHistory()
    {
        lock (gate) { Store.ClearHistory(); engine.Reset(); lastSchedule = default; }
    }
    public void Dispose()
    {
        probe.BoundaryChanged -= Boundary;
        try { Tick(); } catch { }
        cancellation.Cancel();
        try { tasks?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        probe.Dispose(); http.Dispose(); Store.Dispose(); cancellation.Dispose();
    }
}
