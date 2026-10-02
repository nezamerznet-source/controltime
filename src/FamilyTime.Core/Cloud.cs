using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FamilyTime.Core;

public sealed record CloudSession(DateTimeOffset Start, DateTimeOffset End, double ActiveSeconds, IReadOnlyList<UsageRow> Activities);
public sealed record CloudDay(string Day, long Revision, double ActiveSeconds, double IdleSeconds,
    double UnknownSeconds, double BackgroundSeconds, double BrowserUnknownSeconds,
    IReadOnlyList<UsageRow> Activities, IReadOnlyList<UsageRow> Categories, IReadOnlyList<CloudSession>? Sessions);
public sealed record CloudMetadata(string ProfileName, string TimeZone, int LimitMinutes, bool LimitEnabled,
    bool Paused, DateTimeOffset? LastObservedAt, bool BrowserConnected, int HistoryDays, int SummaryDays);
public sealed record CloudReceipt(string Day, long Revision);
public sealed record CloudSyncResponse(IReadOnlyList<CloudReceipt> Accepted);
public sealed class CloudError(string message, bool revoked = false) : Exception(message)
{
    public bool Revoked { get; } = revoked;
}

public static class CloudEndpoint
{
    public static string Normalize(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Введите HTTPS-адрес кабинета без пути, например https://familytime.example.com.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static string TimeZone(AppSettings c) => TimeZoneInfo.TryConvertWindowsIdToIanaId(c.TimeZoneId, out var iana) ? iana : c.Zone.Id;
}

public sealed class CloudClient(HttpClient http)
{
    async Task<T> Post<T>(string endpoint, string path, object body, string? token, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, CloudEndpoint.Normalize(endpoint) + path);
        request.Content = JsonContent.Create(body, options: Wire.Json);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CloudError("Доступ компьютера отозван. Создайте новый код в кабинете и подключите его заново.", token is not null);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new CloudError("Код уже использован. Создайте новый код в личном кабинете.");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                throw new CloudError("Проверьте адрес кабинета и код привязки. Код действует 10 минут.");
            if ((int)response.StatusCode == 429) throw new CloudError("Слишком много запросов. Повторим отправку позже.");
            if (!response.IsSuccessStatusCode) throw new CloudError("Кабинет временно недоступен. История сохранена на компьютере.");
            return await response.Content.ReadFromJsonAsync<T>(Wire.Json, timeout.Token).ConfigureAwait(false)
                ?? throw new CloudError("Кабинет вернул пустой ответ. История сохранена.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        { throw new CloudError("Нет связи с кабинетом. История сохранена; отправка повторится автоматически."); }
    }
    public async Task Claim(string endpoint, string code, string deviceId, string token, string name, CancellationToken ct)
    {
        var normalized = code.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalized.Length != 10 || normalized.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Скопируйте 10 символов кода из раздела «Компьютеры» в кабинете.");
        await Post<JsonElement>(endpoint, "/api/device/claim", new { code = normalized, deviceId, token, name }, null, ct);
    }
    public Task<CloudSyncResponse> Sync(string endpoint, string token, CloudMetadata metadata, CloudDay? day, CancellationToken ct) =>
        Post<CloudSyncResponse>(endpoint, "/api/device/sync", new { protocol = 1, metadata, days = day is null ? Array.Empty<CloudDay>() : new[] { day } }, token, ct);
}

public sealed partial class ActivityStore
{
    // Called under the same store lock/transaction as accounting. The queue coalesces
    // one-second observations into one pending day, including while fully offline.
    void MarkCloudDirty(DateOnly day)
    {
        long revision = long.TryParse(GetMeta("cloud-revision"), out var value) ? value + 1 : 1;
        SetMeta("cloud-revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        db.Run("INSERT INTO cloud_dirty_days(day,revision) VALUES(?,?) ON CONFLICT(day) DO UPDATE SET revision=excluded.revision", Day(day), revision);
    }
    public void QueueCloudHistory(DateOnly today)
    {
        lock (gate) Transaction(() =>
        {
            foreach (var row in db.Query("SELECT DISTINCT day FROM daily")) MarkCloudDirty(DateOnly.Parse(row[0]));
            MarkCloudDirty(today);
        });
    }
    public int CloudPendingDays()
    { lock (gate) return int.Parse(db.Query("SELECT COUNT(*) FROM cloud_dirty_days")[0][0]); }
    public CloudDay? NextCloudDay(AppSettings settings, DateTimeOffset now, bool includeToday = true)
    {
        lock (gate)
        {
            var today = Format.Day(now, settings.Zone);
            var row = db.Query("SELECT day,revision FROM cloud_dirty_days WHERE (?=1 OR day<>?) ORDER BY (day=?) DESC,day LIMIT 1", includeToday ? 1 : 0, Day(today), Day(today)).FirstOrDefault();
            if (row is null) return null;
            var day = DateOnly.Parse(row[0]); var summary = Summarize(day, day);
            var start = Format.Midnight(day, settings.Zone); var end = Format.Midnight(day.AddDays(1), settings.Zone);
            // Older aggregate totals survive local detail retention. Never replace
            // previously uploaded cloud sessions with an empty pruned local history.
            var sessions = start < now.AddDays(-settings.HistoryDays) ? null : CloudSessions(GetSegments(start, end), settings.SessionMinutes);
            return new(row[0], long.Parse(row[1]), summary.ActiveSeconds, summary.IdleSeconds, summary.UnknownSeconds,
                summary.BackgroundSeconds, summary.BrowserUnknownSeconds, summary.Activities, summary.Categories, sessions);
        }
    }
    public void AcknowledgeCloud(CloudReceipt receipt)
    { lock (gate) db.Run("DELETE FROM cloud_dirty_days WHERE day=? AND revision=?", receipt.Day, receipt.Revision); }
    public static IReadOnlyList<CloudSession> CloudSessions(IReadOnlyList<ActivitySlice> slices, int gapMinutes)
    {
        var groups = new List<List<ActivitySlice>>();
        foreach (var s in slices.Where(s => s.Kind == ActivityKind.Active && !s.Background).OrderBy(s => s.Start))
        {
            if (groups.Count == 0 || s.Start - groups[^1][^1].End >= TimeSpan.FromMinutes(gapMinutes)) groups.Add([]);
            groups[^1].Add(s);
        }
        return groups.Select(group => new CloudSession(group[0].Start, group[^1].End, group.Sum(s => s.Seconds),
            group.GroupBy(s => s.Domain.Length > 0 ? "site:" + s.Domain : "app:" + s.AppKey)
                .Select(g => new UsageRow(g.Key, g.First().Domain.Length > 0 ? g.First().Domain : g.First().AppName,
                    g.Select(s => s.Category).Distinct().Count() == 1 ? g.First().Category : "Разные категории", g.Sum(s => s.Seconds)))
                .OrderByDescending(s => s.Seconds).ToArray())).ToArray();
    }
}
