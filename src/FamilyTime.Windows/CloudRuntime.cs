using System.Net.Http;
using System.Security.Cryptography;

namespace FamilyTime.Windows;

public sealed partial class AppRuntime
{
    // Never forward the pairing secret or device token to a redirect target.
    private readonly HttpClient cloudHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    public string CloudStatus { get; private set; } = "Кабинет не подключён.";
    public DateTimeOffset? CloudLastSync { get; private set; }

    public async Task ConnectCloud(string endpoint, string code)
    {
        endpoint = CloudEndpoint.Normalize(endpoint);
        AppSettings pending; string token;
        lock (gate)
        {
            if (config.CloudLinked) throw new ArgumentException("Сначала отключите текущий кабинет.");
            // Persist before the request: a lost successful response is safe to retry,
            // even after restarting Windows. The server recognises the same claim.
            if (config.CloudUrl != endpoint || config.ProtectedCloudToken.Length == 0 || config.CloudDeviceId.Length == 0)
            {
                token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                config = config with { CloudUrl = endpoint, CloudDeviceId = Guid.NewGuid().ToString(), ProtectedCloudToken = SecretStore.Protect(token) };
                Store.SaveSettings(config);
            }
            else token = SecretStore.Unprotect(config.ProtectedCloudToken);
            pending = config;
        }
        await new CloudClient(cloudHttp).Claim(endpoint, code, pending.CloudDeviceId, token, Environment.MachineName, cancellation.Token);
        lock (gate)
        {
            if (config.CloudDeviceId != pending.CloudDeviceId) return;
            Store.QueueCloudHistory(Format.Day(DateTimeOffset.UtcNow, config.Zone));
            config = config with { CloudLinked = true }; Store.SaveSettings(config);
            CloudStatus = "Подключено. Загружаем сохранённую историю…";
        }
    }

    public void DisconnectCloud()
    {
        lock (gate)
        {
            config = config with { CloudLinked = false, CloudDeviceId = "", ProtectedCloudToken = "" };
            Store.SaveSettings(config); CloudLastSync = null; CloudStatus = "Отправка в кабинет отключена. История в кабинете сохранена.";
        }
    }

    async Task CloudLoop()
    {
        var nextToday = DateTimeOffset.MinValue; var nextHeartbeat = DateTimeOffset.MinValue;
        var retryAt = DateTimeOffset.MinValue; int failures = 0; string identity = "";
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                var c = Settings;
                if (!c.CloudLinked || !c.SetupCompleted) continue;
                if (identity != c.CloudDeviceId)
                { identity = c.CloudDeviceId; nextToday = nextHeartbeat = retryAt = DateTimeOffset.MinValue; failures = 0; }
                var now = DateTimeOffset.UtcNow; if (now < retryAt) continue;
                try
                {
                    var day = Store.NextCloudDay(c, now, now >= nextToday);
                    if (day is null && now < nextHeartbeat) continue;
                    var metadata = new CloudMetadata(c.ProfileName, CloudEndpoint.TimeZone(c), c.LimitMinutes, c.LimitEnabled,
                        Paused, LastObservation, Browsers.Connected(now), c.HistoryDays, c.SummaryDays);
                    var receipt = await new CloudClient(cloudHttp).Sync(c.CloudUrl, SecretStore.Unprotect(c.ProtectedCloudToken), metadata, day, cancellation.Token);
                    if (day is not null && (receipt.Accepted is null || !receipt.Accepted.Any(r => r.Day == day.Day && r.Revision == day.Revision)))
                        throw new CloudError("Кабинет не подтвердил сохранение. Отправка будет повторена.");
                    lock (gate)
                    {
                        if (config.CloudDeviceId != c.CloudDeviceId || !config.CloudLinked) continue;
                        if (day is not null) Store.AcknowledgeCloud(new(day.Day, day.Revision));
                        CloudLastSync = DateTimeOffset.UtcNow;
                        CloudStatus = $"На связи. Последняя отправка: {CloudLastSync.Value.LocalDateTime:dd.MM HH:mm}. Ожидают отправки дней: {Store.CloudPendingDays()}.";
                    }
                    failures = 0; nextHeartbeat = now.AddSeconds(60);
                    // Catch up old history quickly without uploading today's live
                    // one-second observations on every iteration or starving history.
                    if (day?.Day == Format.Day(now, c.Zone).ToString("yyyy-MM-dd")) nextToday = now.AddSeconds(60);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        if (config.CloudDeviceId != c.CloudDeviceId) continue;
                        CloudStatus = ex is CloudError ? ex.Message : "Не удалось отправить историю в кабинет. Локальный учёт продолжается.";
                        if (ex is CloudError { Revoked: true })
                        {
                            config = config with { CloudLinked = false, CloudDeviceId = "", ProtectedCloudToken = "" };
                            Store.SaveSettings(config);
                        }
                    }
                    failures = Math.Min(failures + 1, 5); retryAt = now.AddSeconds(Math.Min(300, 15 * Math.Pow(2, failures - 1)));
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
