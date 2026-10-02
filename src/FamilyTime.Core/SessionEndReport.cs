namespace FamilyTime.Core;

public static class SessionEndReport
{
    // Persist the snapshot before any network call. Repeated callbacks for the same
    // application session use the same ID, including after successful delivery.
    public static string? Enqueue(ActivityStore store, AppSettings settings, DateTimeOffset at,
        bool loggingOff, string eventId)
    {
        if (!settings.SetupCompleted || !settings.ShutdownReport || settings.ParentChatId == 0) return null;
        var day = Format.Day(at, settings.Zone);
        var summary = store.Summarize(day, day);
        var longest = ActivityStore.LongestActive(store.GetSegments(Format.Midnight(day, settings.Zone), at));
        string state = loggingOff ? "Windows начинает выход из учётной записи."
            : "Windows начинает выключение или перезагрузку компьютера.";
        var body = state + "\n\n" + Reports.Render(summary, settings, at,
            "итог за сегодня перед завершением работы Windows", longest: longest)
            + "\n\nОтчёт сохранён в этом чате. Новые ответы доступны, когда компьютер включён и Family Time работает."
            + "\nЭто сообщение о начале завершения работы; Windows ещё может отменить его.";
        string id = "shutdown:" + eventId;
        store.Enqueue(id, settings.ParentChatId, settings.TelegramGeneration, body, "shutdown", at, true);
        return id;
    }
}
