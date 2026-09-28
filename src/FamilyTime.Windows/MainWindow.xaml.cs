using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FamilyTime.Windows;

public partial class MainWindow : Window
{
    private readonly AppRuntime runtime;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public bool AllowClose { get; set; }
    public MainWindow(AppRuntime runtime)
    {
        InitializeComponent(); this.runtime = runtime;
        RuleCategory.ItemsSource = Categories.All; RuleCategory.SelectedIndex = 0;
        FromDate.SelectedDate = DateTime.Today.AddDays(-6); ToDate.SelectedDate = DateTime.Today;
        timer.Tick += (_, _) => { if (IsVisible) Refresh(); }; timer.Start();
        Loaded += (_, _) =>
        {
            Refresh(); RefreshRules(); HistoryClick(this, new RoutedEventArgs());
            if (!runtime.Settings.SetupCompleted) Dispatcher.BeginInvoke(() => OpenSettings());
        };
    }
    private static object[] Rows(Summary summary, bool categories = false) => (categories ? summary.Categories : summary.Activities)
        .Select(r => (object)new { r.Key, r.Name, r.Category, Duration = Format.Duration(r.Seconds), Percent = summary.ActiveSeconds <= 0 ? 0 : 100 * r.Seconds / summary.ActiveSeconds }).ToArray();
    public void Refresh()
    {
        try
        {
            var c = runtime.Settings; var today = Format.Day(DateTimeOffset.UtcNow, c.Zone);
            var s = runtime.Store.Summarize(today, today);
            ProfileText.Text = $"{c.ProfileName}  ·  {today:dd.MM.yyyy}  ·  {c.TimeZoneId}";
            LiveStatus.Text = runtime.State; PauseButton.Content = runtime.Paused ? "Продолжить учёт" : "Пауза";
            ConnectionText.Text = (runtime.Browsers.Connected(DateTimeOffset.UtcNow) ? "Браузер подключён" : "Браузер без связи") + "   ·   " + runtime.Telegram.Status;
            ActiveText.Text = Format.Duration(s.ActiveSeconds); BackgroundText.Text = Format.Duration(s.BackgroundSeconds);
            BudgetLabel.Text = c.LimitEnabled && s.Overrun(c) > 0 ? "Превышение" : "Осталось на сегодня";
            BudgetText.Text = !c.LimitEnabled ? "—" : Format.Duration(s.Overrun(c) > 0 ? s.Overrun(c) : s.Remaining(c));
            BudgetText.Foreground = new SolidColorBrush(c.LimitEnabled && s.ActiveSeconds >= c.LimitMinutes * 60 ? Color.FromRgb(194, 65, 12) : Color.FromRgb(37, 99, 235));
            LimitText.Text = c.LimitEnabled ? $"Лимит: {Format.Duration(c.LimitMinutes * 60)} · без блокировки" : "Дневной лимит выключен";
            LimitProgress.Value = c.LimitEnabled ? Math.Min(100, s.ActiveSeconds / (c.LimitMinutes * 60) * 100) : 0;
            var notes = new List<string>();
            if (!c.SetupCompleted) notes.Add("Откройте настройки, чтобы начать учёт.");
            if (s.BrowserUnknownSeconds > 0) notes.Add($"Браузер без детализации: {Format.Duration(s.BrowserUnknownSeconds)}.");
            if (s.UnknownSeconds > 0) notes.Add("Есть паузы или пропуски наблюдения; фактическое время может быть больше.");
            if (runtime.Error is not null) notes.Add(runtime.Error);
            QualityText.Text = notes.Count > 0 ? string.Join(" ", notes) : "В итог входит одно приложение на переднем плане. Фоновое видео не удваивает расход лимита.";
            TodayActivities.ItemsSource = Rows(s); TodayCategories.ItemsSource = Rows(s, true);
            EmptyText.Visibility = s.Activities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RestText.Text = $"Предполагаемый простой: {Format.Duration(s.IdleSeconds)}. Отсутствие клавиатуры и мыши само по себе не доказывает, что ребёнок отошёл.";
            SupportButton.Visibility = Branding.Valid(Branding.Support(c)) ? Visibility.Visible : Visibility.Collapsed;
            RepositoryButton.Visibility = Branding.Valid(Branding.Repository(c)) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { FeedbackText.Text = "Не удалось обновить статистику. Проверьте доступ к папке данных."; }
    }
    void OnClosing(object? sender, CancelEventArgs e) { if (!AllowClose) { e.Cancel = true; Hide(); } }
    void PauseClick(object sender, RoutedEventArgs e) => Guard(() => { runtime.TogglePause(); Refresh(); });
    void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings();
    void OpenSettings() { new SettingsWindow(runtime) { Owner = this }.ShowDialog(); Refresh(); RefreshRules(); }
    void SendClick(object sender, RoutedEventArgs e) => Guard(() => { runtime.SendCurrentReport(); FeedbackText.Text = "Отчёт поставлен в очередь Telegram."; });
    void CopyClick(object sender, RoutedEventArgs e) => Guard(() => { Clipboard.SetText(runtime.CurrentReport(false)); FeedbackText.Text = "Отчёт скопирован."; });
    void HistoryClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (FromDate.SelectedDate is not DateTime from || ToDate.SelectedDate is not DateTime to || from > to) throw new ArgumentException("Выберите корректный период.");
        var summary = runtime.Store.Summarize(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
        HistorySummary.Text = $"Учтено: {Format.Duration(summary.ActiveSeconds)}   ·   Видео в фоне: {Format.Duration(summary.BackgroundSeconds)}";
        if (summary.UnknownSeconds > 0) HistorySummary.Text += "   ·   Есть пропуски";
        HistoryGrid.ItemsSource = Rows(summary);
    });
    void RefreshRules() => RulesGrid.ItemsSource = runtime.Settings.CategoryRules.OrderBy(p => p.Key).ToArray();
    void SaveRuleClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        string key = RuleKey.Text.Trim().ToLowerInvariant();
        if (RuleKind.SelectedIndex == 1) key = Categories.DomainOnly(key);
        else if (key.EndsWith(".exe")) key = key[..^4];
        if (key.Length is 0 or > 200 || key.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '?' or '#')) throw new ArgumentException("Введите имя процесса или домен без пути и параметров.");
        key = (RuleKind.SelectedIndex == 1 ? "site:" : "app:") + key;
        var rules = new Dictionary<string, string>(runtime.Settings.CategoryRules) { [key] = (string)RuleCategory.SelectedItem };
        runtime.UpdateSettings(c => c with { CategoryRules = rules }); RefreshRules(); FeedbackText.Text = "Правило сохранено для новых интервалов.";
    });
    void DeleteRuleClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (RulesGrid.SelectedItem is not KeyValuePair<string, string> row) return;
        var rules = new Dictionary<string, string>(runtime.Settings.CategoryRules); rules.Remove(row.Key);
        runtime.UpdateSettings(c => c with { CategoryRules = rules }); RefreshRules();
    });
    void SupportClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenHttps(Branding.Support(runtime.Settings)));
    void RepositoryClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenHttps(Branding.Repository(runtime.Settings)));
    void DocsClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenFolder(Path.Combine(AppContext.BaseDirectory, "docs")));
    void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(ex is ArgumentException or InvalidOperationException ? ex.Message : "Не удалось выполнить действие. Попробуйте ещё раз и проверьте настройки.", "Family Time", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
