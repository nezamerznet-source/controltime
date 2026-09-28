namespace FamilyTime.Core;

public sealed class AccountingEngine
{
    private Observation? previous;
    public IReadOnlyList<ActivitySlice> Observe(Observation current, TimeSpan idleThreshold)
    {
        var old = previous;
        previous = current;
        if (old is null || current.At <= old.At) return [];
        var wall = (current.At - old.At).TotalSeconds;
        var mono = current.MonotonicSeconds - old.MonotonicSeconds;
        var list = new List<ActivitySlice>();
        if (old.Paused || old.Locked || old.AppKey.Length == 0)
        {
            var kind = old.Paused ? ActivityKind.Paused : old.Locked ? ActivityKind.Locked : ActivityKind.Unknown;
            list.Add(new(old.At, current.At, kind, "", "", "", "Другое"));
            return list;
        }
        if (mono < 0 || wall > 15 || Math.Abs(wall - mono) > 3)
            return [new(old.At, current.At, ActivityKind.Unknown, "", "Нет наблюдений", "", "Другое")];
        var cutoff = old.ForegroundMedia ? current.At : old.LastInput + idleThreshold;
        var activeEnd = cutoff < current.At ? cutoff : current.At;
        if (activeEnd > old.At)
            list.Add(new(old.At, activeEnd, ActivityKind.Active, old.AppKey, old.AppName, old.Domain, old.Category));
        var idleStart = activeEnd > old.At ? activeEnd : old.At;
        if (idleStart < current.At)
            list.Add(new(idleStart, current.At, ActivityKind.Idle, "", "Простой", "", "Другое"));
        if (old.BackgroundMedia)
            list.Add(new(old.At, current.At, ActivityKind.Active, "background-media", "YouTube в фоне", "youtube.com", "Видео", true));
        return list;
    }
    public void Reset() => previous = null;
}

public static class LimitPolicy
{
    public static LimitEvent? Evaluate(Summary s, AppSettings c)
    {
        if (!c.SetupCompleted || !c.LimitEnabled) return null;
        string prefix = $"limit:{s.To:yyyy-MM-dd}:{c.LimitRevision}";
        string date = s.To.ToString("dd.MM.yyyy");
        if (s.ActiveSeconds >= c.LimitMinutes * 60)
        {
            var title = s.Overrun(c) >= 60 ? "Дневной лимит превышен" : "Дневной лимит достигнут";
            var body = $"{c.ProfileName} · {date}\nЛимит: {Format.Duration(c.LimitMinutes * 60)}\nУчтено: {Format.Duration(s.ActiveSeconds)}";
            if (s.Overrun(c) >= 60) body += $"\nПревышение: {Format.Duration(s.Overrun(c))}";
            body += "\nУчёт продолжается.";
            if (s.UnknownSeconds > 0) body += "\nЕсть пропуски наблюдения.";
            return new(prefix + ":reached", title, body, true);
        }
        if (c.WarningMinutes > 0 && s.Remaining(c) <= c.WarningMinutes * 60)
            return new(prefix + ":warning", "Время подходит к концу",
                $"На сегодня осталось {Format.Duration(s.Remaining(c))}. Лимит: {Format.Duration(c.LimitMinutes * 60)}.", false);
        return null;
    }
}

public sealed class BrowserRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, (BrowserState State, DateTimeOffset At)> states = new();
    public void Accept(BrowserState state, DateTimeOffset now)
    {
        if (state.Version != Wire.Version || state.Browser is not ("chrome" or "msedge") ||
            !Guid.TryParse(state.InstanceId, out _) || state.Sequence < 0) return;
        var clean = state with { Domain = Categories.DomainOnly(state.Domain ?? "") };
        if (clean.ForegroundPlaying && clean.Domain is not ("youtube.com" or "m.youtube.com"))
            clean = clean with { ForegroundPlaying = false };
        lock (gate)
        {
            foreach (var key in states.Where(x => now - x.Value.At > TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray()) states.Remove(key);
            if (states.Count >= 16 && !states.ContainsKey(clean.InstanceId)) return;
            if (states.TryGetValue(clean.InstanceId, out var old) && clean.Sequence <= old.State.Sequence) return;
            states[clean.InstanceId] = (clean, now);
        }
    }
    public (string Domain, bool Foreground, bool Background, bool Connected) Resolve(string app, DateTimeOffset now)
    {
        lock (gate)
        {
            var fresh = states.Values.Where(x => now - x.At <= TimeSpan.FromSeconds(7)).Select(x => x.State).ToArray();
            var matching = fresh.Where(x => x.Browser == app && x.Focused).ToArray();
            var selected = matching.Length == 1 ? matching[0] : null;
            bool background = fresh.Any(x => x.BackgroundPlaying || (x.ForegroundPlaying && x != selected));
            if (!Categories.IsBrowser(app)) return ("", false, background || fresh.Any(x => x.ForegroundPlaying), false);
            return (selected?.Domain ?? "", selected?.ForegroundPlaying ?? false, background, selected is not null);
        }
    }
    public bool Connected(DateTimeOffset now) { lock (gate) return states.Values.Any(x => now - x.At < TimeSpan.FromSeconds(7)); }
}
