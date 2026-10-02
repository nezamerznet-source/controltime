using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyTime.Core;

public sealed class TelegramError(string message, bool permanent = false, int retryAfter = 0) : Exception(message)
{
    public bool Permanent { get; } = permanent;
    public int RetryAfter { get; } = retryAfter;
}

public interface ITelegramApi
{
    Task<JsonElement[]> Poll(string token, long offset, CancellationToken ct);
    Task<long> Send(string token, long chat, string body, bool keyboard, CancellationToken ct);
    Task<string> Username(string token, CancellationToken ct);
}

public sealed class TelegramApi(HttpClient http) : ITelegramApi
{
    async Task<JsonElement> Call(string token, string method, object data, CancellationToken ct)
    {
        if (token.Length is < 15 or > 200 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-')))
            throw new TelegramError("Проверьте токен бота.", true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", data, Wire.Json, timeout.Token);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = json.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var code = root.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)response.StatusCode;
                var wait = root.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) ? r.GetInt32() : 0;
                throw new TelegramError(code switch
                {
                    401 => "Неверный токен Telegram.", 403 => "Бот заблокирован или чат недоступен.",
                    409 => "Этот бот уже используется другим получателем обновлений или webhook.",
                    429 => "Telegram попросил повторить позже.", _ => "Telegram временно не принял запрос."
                }, code is 400 or 401 or 403, wait);
            }
            return root.GetProperty("result").Clone();
        }
        catch (TelegramError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { throw new TelegramError("Не удалось связаться с Telegram. Проверьте интернет; данные сохранены."); }
    }
    public async Task<JsonElement[]> Poll(string token, long offset, CancellationToken ct) =>
        (await Call(token, "getUpdates", new { offset, timeout = 25, allowed_updates = new[] { "message" } }, ct)).EnumerateArray().Select(x => x.Clone()).ToArray();
    public async Task<long> Send(string token, long chat, string body, bool keyboard, CancellationToken ct)
    {
        object? markup = keyboard ? new { keyboard = new[] { new[] { "Отчёт за сегодня", "Сколько осталось" } }, resize_keyboard = true, is_persistent = true } : null;
        var result = await Call(token, "sendMessage", new { chat_id = chat, text = body, reply_markup = markup, link_preview_options = new { is_disabled = true } }, ct);
        return result.GetProperty("message_id").GetInt64();
    }
    public async Task<string> Username(string token, CancellationToken ct) => (await Call(token, "getMe", new { }, ct)).GetProperty("username").GetString() ?? "";
}

public static class Pairing
{
    public static string CreateCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    public static bool Matches(string code, string hash)
    {
        if (code.Length != 24 || hash.Length != 64) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(code)), Encoding.ASCII.GetBytes(hash));
    }
}

public sealed class TelegramWorker(ActivityStore store, ITelegramApi api, Func<AppSettings> settings,
    Func<string> token, Func<bool, string> report, Func<long, long, string, bool> bind)
{
    public string Status { get; private set; } = "Telegram не подключён";
    private DateTimeOffset lastReply;
    private readonly SemaphoreSlim sending = new(1, 1);
    public async Task Run(CancellationToken ct)
    {
        await Task.WhenAll(PollLoop(ct), SendLoop(ct));
    }
    async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var config = settings(); var key = token();
                if (!config.SetupCompleted || key.Length == 0) { Status = "Telegram не подключён"; await Task.Delay(2000, ct); continue; }
                var updates = await api.Poll(key, store.Offset(config.TelegramGeneration), ct);
                if (settings().TelegramGeneration != config.TelegramGeneration) continue;
                Status = config.ParentChatId == 0 ? "Ожидается привязка родителя" : "Telegram на связи";
                foreach (var update in updates)
                {
                    if (settings().TelegramGeneration != config.TelegramGeneration) break;
                    Handle(update, config.TelegramGeneration, DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TelegramError e) { Status = e.Message; await Delay(Math.Max(e.RetryAfter, e.Permanent ? 60 : 10), ct); }
            catch { Status = "Ошибка обработки Telegram; запрос будет повторён."; await Delay(10, ct); }
        }
    }
    public void Handle(JsonElement update, int generation, DateTimeOffset now)
    {
        if (!update.TryGetProperty("update_id", out var uid)) return;
        long id = uid.GetInt64(); var config = settings();
        if (config.TelegramGeneration != generation) return;
        if (store.CommandSeen(generation, id)) { store.FinishCommand(generation, id, () => { }); return; }
        string? body = null; long chatId = 0;
        if (update.TryGetProperty("message", out var m) && m.TryGetProperty("chat", out var chat)
            && chat.GetProperty("type").GetString() == "private" && m.TryGetProperty("from", out var user)
            && m.TryGetProperty("text", out var textElement))
        {
            var text = (textElement.GetString() ?? "").Trim();
            chatId = chat.GetProperty("id").GetInt64(); var userId = user.GetProperty("id").GetInt64();
            if (config.ParentChatId == 0 && text.StartsWith("/start ", StringComparison.Ordinal) &&
                now <= config.PairingExpires && Pairing.Matches(text[7..].Trim(), config.PairingHash))
            {
                if (bind(chatId, userId, text[7..].Trim())) body = "Family Time подключён.\nКнопка «Отчёт за сегодня» показывает учтённое время. «Сколько осталось» — остаток дневного лимита.\nОтветы доступны, пока компьютер включён и приложение работает.";
            }
            else if (config.ParentChatId == chatId && config.ParentUserId == userId)
            {
                if (now - lastReply >= TimeSpan.FromSeconds(2))
                {
                    if (text is "/today" or "Отчёт за сегодня" or "Сегодня") body = report(false);
                    else if (text is "/remaining" or "Сколько осталось") body = report(true);
                    else if (text is "/help" or "/start") body = "Отчёт: /today\nОстаток лимита: /remaining\nДневной лимит задаётся в настройках Family Time на компьютере.";
                    if (body is not null)
                    {
                        lastReply = now;
                        if (m.TryGetProperty("date", out var d) && now - DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()) > TimeSpan.FromMinutes(2))
                            body = "Запрос обработан после задержки. Ниже данные на момент формирования ответа.\n\n" + body;
                    }
                }
            }
        }
        var reply = body; var destination = chatId;
        store.FinishCommand(generation, id, () =>
        {
            if (reply is not null) store.Enqueue($"command:{generation}:{id}", destination, generation, reply, "request", now, true);
        });
    }
    // The regular sender and the last shutdown attempt share one gate: a queued
    // message cannot be sent twice concurrently. Cancellation leaves it durable.
    public async Task<bool> SendPending(CancellationToken ct, string? id = null)
    {
        await sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var config = settings(); var key = token();
            var item = store.NextOutgoing(DateTimeOffset.UtcNow, id);
            if (item is null || key.Length == 0 || config.ParentChatId == 0) return false;
            if (item.Generation != config.TelegramGeneration || item.ChatId != config.ParentChatId)
            { store.Retry(item.Id, DateTimeOffset.UtcNow, true); return false; }
            try
            {
                var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id)))[..8];
                var body = item.Body;
                if (DateTimeOffset.UtcNow - item.Created > TimeSpan.FromMinutes(5)) body += "\nОтправлено с задержкой; время события/среза указано выше.";
                body += "\n№ " + suffix;
                if (body.Length > 4000) body = body[..3980] + "…";
                long messageId = await api.Send(key, item.ChatId, body, item.Keyboard, ct).ConfigureAwait(false);
                store.MarkSent(item.Id, messageId);
                return true;
            }
            catch (TelegramError error)
            {
                Status = error.Message;
                int wait = Math.Max(error.RetryAfter, (int)Math.Min(3600, 5 * Math.Pow(2, Math.Min(item.Attempts, 10))));
                store.Retry(item.Id, DateTimeOffset.UtcNow.AddSeconds(wait), error.Permanent);
                return false;
            }
        }
        finally { sending.Release(); }
    }
    async Task SendLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendPending(ct).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { Status = "Не удалось отправить сообщение; оно сохранено в очереди."; await Delay(10, ct); }
        }
    }
    static async Task Delay(int seconds, CancellationToken ct) { try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (OperationCanceledException) { } }
}
