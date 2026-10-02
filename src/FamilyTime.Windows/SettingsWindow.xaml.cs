using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FamilyTime.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppRuntime runtime;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool busy;
    internal async Task CaptureChecks(string directory)
    {
        for (int i = 0; i < SettingsTabs.Items.Count; i++)
        {
            SettingsTabs.SelectedIndex = i; UpdateLayout(); await Task.Delay(60);
            UiCheck.Capture(this, Path.Combine(directory, $"settings-{i}.png"));
        }
        SettingsTabs.SelectedIndex = 0;
    }
    public SettingsWindow(AppRuntime runtime)
    {
        InitializeComponent(); this.runtime = runtime;
        var c = runtime.Settings;
        Heading.Text = c.SetupCompleted ? "Настройки семьи" : "Добро пожаловать в Family Time";
        SaveButton.Content = c.SetupCompleted ? "Сохранить" : "Сохранить и начать учёт";
        AccountText.Text = "Учётная запись Windows: " + Environment.UserName;
        CloudUrlBox.Text = c.CloudUrl;
        ProfileBox.Text = c.ProfileName; AutoStartBox.IsChecked = c.AutoStart; LimitEnabledBox.IsChecked = c.LimitEnabled;
        LimitBox.Text = c.LimitMinutes.ToString(); WarningBox.Text = c.WarningMinutes.ToString();
        IdleBox.Text = c.IdleMinutes.ToString(); SessionBox.Text = c.SessionMinutes.ToString();
        ZoneText.Text = "Часовой пояс учёта: " + c.TimeZoneId + ". Фиксируется при первом запуске.";
        DailyReportBox.IsChecked = c.DailyReport; SessionReportBox.IsChecked = c.SessionReport; WeeklyReportBox.IsChecked = c.WeeklyReport;
        EveningReportBox.IsChecked = c.EveningReport; EveningHourBox.Text = c.EveningHour.ToString();
        HistoryDaysBox.Text = c.HistoryDays.ToString(); SummaryDaysBox.Text = c.SummaryDays.ToString();
        DonateButton.Visibility = Branding.Valid(Branding.Support(c)) ? Visibility.Visible : Visibility.Collapsed;
        RefreshProtection();
        timer.Tick += (_, _) => RefreshStatus(); timer.Start(); Closed += (_, _) => timer.Stop();
        RefreshStatus();
    }
    static int Number(TextBox box, string name)
    {
        if (!int.TryParse(box.Text.Trim(), out int value)) throw new ArgumentException(name + ": введите целое число.");
        return value;
    }
    void Save()
    {
        runtime.UpdateSettings(c => c with
        {
            SetupCompleted = true, ProfileName = ProfileBox.Text.Trim(), AutoStart = AutoStartBox.IsChecked == true,
            LimitEnabled = LimitEnabledBox.IsChecked == true, LimitMinutes = Number(LimitBox, "Лимит"), WarningMinutes = Number(WarningBox, "Предупреждение"),
            IdleMinutes = Number(IdleBox, "Простой"), SessionMinutes = Number(SessionBox, "Конец сеанса"),
            DailyReport = DailyReportBox.IsChecked == true, SessionReport = SessionReportBox.IsChecked == true, WeeklyReport = WeeklyReportBox.IsChecked == true,
            EveningReport = EveningReportBox.IsChecked == true, EveningHour = Number(EveningHourBox, "Час отчёта"),
            HistoryDays = Number(HistoryDaysBox, "Срок истории"), SummaryDays = Number(SummaryDaysBox, "Срок итогов")
        });
        Heading.Text = "Настройки семьи";
        SaveButton.Content = "Сохранить изменения"; StatusText.Text = "Настройки сохранены. Учёт работает. Telegram можно подключить на соседней вкладке.";
    }
    void RefreshStatus()
    {
        try
        {
            var c = runtime.Settings;
            CloudStatusText.Text = runtime.CloudStatus;
            CloudConnectButton.IsEnabled = !busy && !c.CloudLinked;
            CloudDisconnectButton.IsEnabled = !busy && (c.CloudLinked || c.ProtectedCloudToken.Length > 0);
            CloudUrlBox.IsEnabled = CloudCodeBox.IsEnabled = !busy && !c.CloudLinked;
            ExtensionStatus.Text = runtime.Browsers.Connected(DateTimeOffset.UtcNow) ? "Расширение подключено, данные поступают." : "Связи пока нет. Откройте браузер с установленным расширением.";
            TelegramStatus.Text = (c.ParentChatId != 0 ? $"Родитель привязан · @{c.BotUsername}\n" : "") + runtime.Telegram.Status;
            if (c.ParentChatId != 0) { PairPanel.Visibility = Visibility.Collapsed; PairLink.Text = ""; }
            else if (PairPanel.Visibility == Visibility.Visible && DateTimeOffset.UtcNow > c.PairingExpires) TelegramStatus.Text = "Ссылка привязки истекла. Проверьте токен заново, чтобы получить новую.";
            var q = runtime.Store.QueueCounts(); QueueStatus.Text = $"Telegram: ожидает отправки — {q.Pending}, требует повтора — {q.Errors}.";
        }
        catch { StatusText.Text = "Не удалось прочитать состояние подключения."; }
    }
    internal void SaveAndContinue() { Save(); ((App)Application.Current).Dashboard.Refresh(); }
    void SaveClick(object sender, RoutedEventArgs e) => Guard(SaveAndContinue);
    void CloseClick(object sender, RoutedEventArgs e) => Close();
    void TestNoticeClick(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowNotification("Family Time", "Проверка уведомления. Так приложение напомнит о дневном лимите.");
    void ExtensionFolderClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenFolder(Path.Combine(AppContext.BaseDirectory, "extension")));
    void ExtensionPathClick(object sender, RoutedEventArgs e) => Guard(() => { Clipboard.SetText(Path.Combine(AppContext.BaseDirectory, "extension")); StatusText.Text = "Путь скопирован."; });
    void RegisterClick(object sender, RoutedEventArgs e) => Guard(() => { WindowsIntegration.RegisterBrowser(); StatusText.Text = "Связь зарегистрирована. Перезагрузите расширение в браузере."; });
    void BotFatherClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenHttps("https://t.me/BotFather"));
    async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        busy = true; ConnectButton.IsEnabled = false;
        try
        {
            Save(); StatusText.Text = "Проверяем токен Telegram…";
            var pair = await runtime.ConnectTelegram(TokenBox.Password);
            TokenBox.Clear(); PairLink.Text = $"https://t.me/{pair.Username}?start={pair.Code}"; PairPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Теперь откройте ссылку с аккаунта родителя.";
        }
        catch (Exception ex) { StatusText.Text = ex is TelegramError or ArgumentException ? ex.Message : "Не удалось подключить Telegram. Проверьте токен и интернет."; }
        finally { busy = false; ConnectButton.IsEnabled = true; RefreshStatus(); }
    }
    void CopyPairClick(object sender, RoutedEventArgs e) => Guard(() => { Clipboard.SetText(PairLink.Text); StatusText.Text = "Ссылка скопирована. Передайте её только родителю."; });
    void OpenPairClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenHttps(PairLink.Text));
    void DisconnectClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (MessageBox.Show("Отключить бота и отменить ожидающие сообщения? Локальная история сохранится.", "Отключение Telegram", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        runtime.DisconnectTelegram(); TokenBox.Clear(); PairLink.Clear(); PairPanel.Visibility = Visibility.Collapsed; RefreshStatus();
    });
    void TestTelegramClick(object sender, RoutedEventArgs e) => Guard(() => { runtime.SendCurrentReport(); StatusText.Text = "Пробный отчёт поставлен в очередь. Проверьте Telegram родителя."; });
    void RetryClick(object sender, RoutedEventArgs e) => Guard(() => { runtime.Store.RetryErrors(); RefreshStatus(); });
    void DataFolderClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenFolder(WindowsIntegration.DataDirectory));
    void ClearClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (MessageBox.Show("Удалить локальные интервалы, дневные итоги и очередь сообщений? Сегодняшний расход лимита обнулится, в том числе в кабинете после синхронизации. Более ранняя облачная история сохранится; удалить её можно в кабинете вместе с компьютером. Настройки и привязка родителя сохранятся. Действие необратимо.", "Удаление истории", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        runtime.ClearHistory(); StatusText.Text = "История и очередь удалены.";
    });
    void DonateClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenHttps(Branding.Support(runtime.Settings)));
    async void CloudConnectClick(object sender, RoutedEventArgs e)
    {
        if (busy) return; busy = true; RefreshStatus();
        try
        {
            Save(); StatusText.Text = "Подключаем личный кабинет…";
            await runtime.ConnectCloud(CloudUrlBox.Text, CloudCodeBox.Text);
            CloudCodeBox.Clear(); StatusText.Text = "Компьютер подключён. Данные появятся в кабинете после синхронизации.";
        }
        catch (Exception ex) { StatusText.Text = ex is CloudError or ArgumentException ? ex.Message : "Не удалось подключить кабинет. Проверьте адрес, код и доступ к интернету."; }
        finally { busy = false; RefreshStatus(); }
    }
    void CloudOpenClick(object sender, RoutedEventArgs e) => Guard(() => WindowsIntegration.OpenHttps(CloudEndpoint.Normalize(CloudUrlBox.Text)));
    void CloudDisconnectClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (MessageBox.Show("Отключить отправку? Уже загруженная история в кабинете останется. Для её удаления откройте раздел «Компьютеры» в кабинете.", "Личный кабинет", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        runtime.DisconnectCloud(); CloudCodeBox.Clear(); RefreshStatus();
    });
    void RefreshProtection()
    {
        ProtectionStatus.Text = runtime.Parent.Required
            ? "Защита включена. Настройки, пауза и выход требуют родительский пароль."
            : "Пароль пока не задан. Любой пользователь может менять настройки и останавливать учёт.";
        RemovePasswordButton.IsEnabled = runtime.Parent.Required;
    }
    void SetPasswordClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!runtime.Parent.Request(this, "Изменить родительский пароль")) return;
        if (NewParentPassword.Password != RepeatParentPassword.Password) throw new ArgumentException("Пароли не совпадают. Введите их заново.");
        string hash = ParentPassword.Create(NewParentPassword.Password);
        runtime.UpdateSettings(c => c with { ParentPasswordHash = hash });
        NewParentPassword.Clear(); RepeatParentPassword.Clear(); RefreshProtection();
        StatusText.Text = "Пароль сохранён. Закройте настройки, чтобы ограничить доступ к ним.";
    });
    void RemovePasswordClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!runtime.Parent.Request(this, "Отключить защиту паролем")) return;
        if (MessageBox.Show(this, "Без пароля ребёнок сможет поставить учёт на паузу и выйти. Отключить защиту?", "Родительский пароль",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        runtime.UpdateSettings(c => c with { ParentPasswordHash = "" }); RefreshProtection();
    });
    void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { StatusText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "Не удалось выполнить действие. Проверьте настройки и доступ к папке данных."; }
    }
}
