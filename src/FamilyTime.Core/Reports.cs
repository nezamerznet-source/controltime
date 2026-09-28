using System.Text;

namespace FamilyTime.Core;

public static class Reports
{
    public static string Render(Summary s, AppSettings c, DateTimeOffset at, string title,
        bool remainingOnly = false, string state = "", double? longest = null, bool includeLimit = true)
    {
        var text = new StringBuilder();
        text.AppendLine($"{c.ProfileName} · {title}");
        text.AppendLine(s.From == s.To ? s.From.ToString("dd.MM.yyyy") : $"{s.From:dd.MM.yyyy} — {s.To:dd.MM.yyyy}");
        text.AppendLine($"Сформировано: {Format.Stamp(at, c.Zone)}");
        text.AppendLine($"Учтено: {Format.Duration(s.ActiveSeconds)}");
        if (includeLimit && c.LimitEnabled && s.From == s.To && s.To == Format.Day(at, c.Zone))
        {
            text.AppendLine($"Лимит сейчас: {Format.Duration(c.LimitMinutes * 60)}");
            text.AppendLine(s.Overrun(c) > 0 ? $"Превышение: {Format.Duration(s.Overrun(c))}" : $"Осталось: {Format.Duration(s.Remaining(c))}");
        }
        else if (includeLimit && !c.LimitEnabled) text.AppendLine("Дневной лимит выключен.");
        if (!remainingOnly)
        {
            text.AppendLine();
            foreach (var row in s.Categories) text.AppendLine($"{row.Name}: {Format.Duration(row.Seconds)}");
            text.AppendLine();
            foreach (var row in s.Activities.Take(8)) text.AppendLine($"• {row.Name} — {Format.Duration(row.Seconds)}");
            if (longest.HasValue) text.AppendLine($"Без перерыва: максимум {Format.Duration(longest.Value)}");
            if (s.BackgroundSeconds > 0) text.AppendLine($"Видео в фоне: {Format.Duration(s.BackgroundSeconds)} (в общий итог не добавлено)");
            if (s.IdleSeconds > 0) text.AppendLine($"Предполагаемый простой: {Format.Duration(s.IdleSeconds)}");
        }
        if (s.BrowserUnknownSeconds > 0) text.AppendLine($"Браузер без детализации: {Format.Duration(s.BrowserUnknownSeconds)}");
        if (s.UnknownSeconds > 0) text.AppendLine("Есть паузы или пропуски наблюдения; фактическое время может быть больше.");
        if (!string.IsNullOrEmpty(state)) text.AppendLine($"Состояние: {state}");
        return text.ToString().TrimEnd();
    }
}
