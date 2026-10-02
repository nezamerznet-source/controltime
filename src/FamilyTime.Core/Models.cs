using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyTime.Core;

public enum ActivityKind { Active, Idle, Locked, Paused, Unknown }

public static class Wire
{
    public const int Version = 1;
    public const string HostName = "org.familytime.activity";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
}

public sealed record ActivitySlice(DateTimeOffset Start, DateTimeOffset End, ActivityKind Kind,
    string AppKey, string AppName, string Domain, string Category, bool Background = false)
{
    public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
}

public sealed record Observation(DateTimeOffset At, double MonotonicSeconds, DateTimeOffset LastInput,
    bool Locked, bool Paused, string AppKey, string AppName, string Domain, string Category,
    bool ForegroundMedia, bool BackgroundMedia, bool BrowserDetailMissing = false);

public sealed record BrowserState
{
    public int Version { get; init; } = Wire.Version;
    public string Browser { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public long Sequence { get; init; }
    public bool Focused { get; init; }
    public string Domain { get; init; } = "";
    public bool ForegroundPlaying { get; init; }
    public bool BackgroundPlaying { get; init; }
}

public sealed record AppSettings
{
    public bool SetupCompleted { get; init; }
    public string ProfileName { get; init; } = "Ребёнок";
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public int IdleMinutes { get; init; } = 5;
    public int SessionMinutes { get; init; } = 15;
    public int LimitMinutes { get; init; } = 120;
    public bool LimitEnabled { get; init; } = true;
    public int WarningMinutes { get; init; } = 10;
    public int LimitRevision { get; init; } = 1;
    public bool DailyReport { get; init; } = true;
    public bool SessionReport { get; init; } = true;
    public bool WeeklyReport { get; init; }
    public bool EveningReport { get; init; }
    public int EveningHour { get; init; } = 21;
    public int HistoryDays { get; init; } = 90;
    public int SummaryDays { get; init; } = 365;
    public bool AutoStart { get; init; } = true;
    public string ParentPasswordHash { get; init; } = "";
    public string CloudUrl { get; init; } = "";
    public string CloudDeviceId { get; init; } = "";
    public string ProtectedCloudToken { get; init; } = "";
    public bool CloudLinked { get; init; }
    public long ParentChatId { get; init; }
    public long ParentUserId { get; init; }
    public string ProtectedToken { get; init; } = "";
    public string BotUsername { get; init; } = "";
    public int TelegramGeneration { get; init; }
    public string PairingHash { get; init; } = "";
    public DateTimeOffset PairingExpires { get; init; }
    public string SupportUrl { get; init; } = "";
    public string RepositoryUrl { get; init; } = "";
    public Dictionary<string, string> CategoryRules { get; init; } = new();
    [JsonIgnore] public TimeZoneInfo Zone
    {
        get { try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); } catch { return TimeZoneInfo.Local; } }
    }
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileName) || ProfileName.Length > 60 || ProfileName.Any(char.IsControl)) return "Имя: от 1 до 60 символов, без переносов строк.";
        if (IdleMinutes is < 1 or > 60) return "Простой: от 1 до 60 минут.";
        if (SessionMinutes is < 1 or > 180) return "Завершение сеанса: от 1 до 180 минут.";
        if (LimitMinutes is < 1 or > 1440) return "Лимит: от 1 до 1440 минут.";
        if (WarningMinutes < 0 || WarningMinutes >= LimitMinutes) return "Предупреждение должно быть меньше лимита; 0 — выключить.";
        if (HistoryDays < 7 || HistoryDays > 365 || SummaryDays < HistoryDays || SummaryDays > 730) return "История: 7–365 дней; итоги — не меньше срока истории, максимум 730.";
        if (EveningHour is < 0 or > 23) return "Час отчёта: от 0 до 23.";
        return null;
    }
}

public static class Categories
{
    public static readonly string[] All = ["Игры", "Видео", "Общение", "Учёба", "Творчество", "Другое"];
    public static string Resolve(string app, string domain, AppSettings settings)
    {
        if (domain.Length > 0 && settings.CategoryRules.TryGetValue("site:" + domain, out var siteRule)) return siteRule;
        if (settings.CategoryRules.TryGetValue("app:" + app, out var rule)) return rule;
        if (domain is "youtube.com" or "m.youtube.com" or "twitch.tv" or "rutube.ru") return "Видео";
        if (app.Contains("minecraft") || app.Contains("roblox") || app.Contains("fortnite") ||
            app is "cs2" or "dota2" or "valorant-win64-shipping" or "gta5" or "eldenring") return "Игры";
        if (app is "discord" or "telegram" or "whatsapp" || domain is "web.telegram.org" or "discord.com") return "Общение";
        if (app is "photoshop" or "krita" or "blender" or "audacity" or "fl64") return "Творчество";
        return "Другое";
    }
    public static bool IsBrowser(string app) => app is "chrome" or "msedge";
    public static string DomainOnly(string value)
    {
        if (value.Length > 253 || value.Any(c => char.IsControl(c) || c is '/' or ':' or '@' or '?' or '#')) return "";
        try
        {
            var host = new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
            if (Uri.CheckHostName(host) != UriHostNameType.Dns) return "";
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }
        catch { return ""; }
    }
}

public sealed record UsageRow(string Key, string Name, string Category, double Seconds, bool Background = false);
public sealed record Summary(DateOnly From, DateOnly To, double ActiveSeconds, double IdleSeconds,
    double UnknownSeconds, double BackgroundSeconds, IReadOnlyList<UsageRow> Activities,
    IReadOnlyList<UsageRow> Categories, double BrowserUnknownSeconds = 0)
{
    public double Remaining(AppSettings c) => Math.Max(0, c.LimitMinutes * 60 - ActiveSeconds);
    public double Overrun(AppSettings c) => Math.Max(0, ActiveSeconds - c.LimitMinutes * 60);
}

public sealed record Outgoing(string Id, long ChatId, int Generation, string Body, string Kind,
    int Attempts, DateTimeOffset Created, bool Keyboard);
public sealed record LimitEvent(string Id, string Title, string Body, bool Reached);

public static class Format
{
    public static string Duration(double seconds)
    {
        var minutes = (int)Math.Floor(Math.Max(0, seconds) / 60);
        if (minutes == 0 && seconds > 0) return "< 1 мин";
        return minutes >= 60 ? $"{minutes / 60} ч {minutes % 60:D2} мин" : $"{minutes} мин";
    }
    public static DateOnly Day(DateTimeOffset value, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
    public static string Stamp(DateTimeOffset value, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(value, zone).ToString("dd.MM.yyyy HH:mm zzz", CultureInfo.InvariantCulture);
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
