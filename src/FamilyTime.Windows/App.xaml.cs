using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FamilyTime.Windows;

public partial class App : System.Windows.Application
{
    private string? startupCheckDirectory;
    private Mutex? singleton;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showRegistration;
    private EventWaitHandle? exitEvent;
    private RegisteredWaitHandle? exitRegistration;
    private bool ownsSingleton;
    private Forms.NotifyIcon? tray;
    private bool handlingUiError;
    private bool startupCheck;
    public AppRuntime Runtime { get; private set; } = null!;
    public MainWindow Dashboard { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteStartupError(args.Exception, startupCheck ? Path.GetTempPath() : WindowsIntegration.DataDirectory);
            args.Handled = true;
            if (startupCheck) { Shutdown(1); return; }
            if (handlingUiError) return;
            handlingUiError = true;
            try
            {
                MessageBox.Show("Ошибка интерфейса: " + args.Exception.GetBaseException().Message +
                    "\n\nДиагностика записана в FamilyTime-startup-error.txt в папке данных. Настройки не удалены.",
                    "Family Time", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { handlingUiError = false; }
        };
        if (e.Args.Contains("--register")) { try { WindowsIntegration.RegisterBrowser(); Shutdown(0); } catch { Shutdown(1); } return; }
        if (e.Args.Contains("--unregister")) { try { if (!AuthorizeMaintenance()) { Shutdown(1); return; } WindowsIntegration.Unregister(); Shutdown(0); } catch { Shutdown(1); } return; }
        if (e.Args.Contains("--shutdown")) { Shutdown(RequestShutdown()); return; }
        if (e.Args.Contains("--startup-check"))
        {
            startupCheck = true;
            startupCheckDirectory = Path.Combine(Path.GetTempPath(), "FamilyTime-check-" + Guid.NewGuid().ToString("N"));
            Dispatcher.BeginInvoke(async () => await CheckStartup());
            return;
        }
        singleton = new Mutex(true, @"Local\FamilyTime." + WindowsIntegration.Identity, out bool first);
        ownsSingleton = first;
        if (!first)
        {
            bool signalled = e.Args.Contains("--tray") || SignalExisting(@"Local\FamilyTime.Show." + WindowsIntegration.Identity);
            if (!signalled) MessageBox.Show("Family Time уже работает, но окно пока не отвечает. Попробуйте значок часов возле системных часов.", "Family Time");
            Shutdown(signalled ? 0 : 1); return;
        }
        try
        {
            Runtime = new AppRuntime();
            Dashboard = new MainWindow(Runtime); MainWindow = Dashboard;
            tray = new Forms.NotifyIcon { Text = "Family Time — семейный учёт времени", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть статистику", null, (_, _) => ShowDashboard());
            menu.Items.Add("Родительские настройки", null, (_, _) => Dispatcher.BeginInvoke(() => { RestoreDashboard(); Dashboard.OpenSettings(); }));
            menu.Items.Add("Пауза / продолжить учёт", null, (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (Runtime.Parent.Request(Dashboard, "Изменить состояние учёта")) Runtime.TogglePause();
            }));
            menu.Items.Add("Выход (остановить учёт)", null, (_, _) => Dispatcher.BeginInvoke(TryExit));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowDashboard(); };
            Runtime.Notify += (title, body) => Dispatcher.BeginInvoke(() => ShowNotification(title, body));
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FamilyTime.Show." + WindowsIntegration.Identity);
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ShowDashboard(), null, Timeout.Infinite, false);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FamilyTime.Exit." + WindowsIntegration.Identity);
            exitRegistration = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => Dispatcher.BeginInvoke(TryExit), null, Timeout.Infinite, false);
            if (Runtime.Settings.SetupCompleted) WindowsIntegration.AutoStart(Runtime.Settings.AutoStart);
            Runtime.Start();
            if (!e.Args.Contains("--tray") || !Runtime.Settings.SetupCompleted) ShowDashboard();
        }
        catch (Exception ex)
        {
            var log = WriteStartupError(ex, WindowsIntegration.DataDirectory);
            var detail = ex.GetBaseException();
            MessageBox.Show($"Family Time не смог запуститься. Существующие данные не удалялись.\n\nПричина: {detail.GetType().Name}: {detail.Message}\n\n" +
                (log is null ? "Не удалось записать журнал ошибки. Сохраните снимок этого окна." : $"Журнал ошибки: {log}"),
                "Family Time", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static string? WriteStartupError(Exception error, string directory)
    {
        foreach (var target in new[] { directory, Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(target);
                var path = Path.Combine(target, "FamilyTime-startup-error.txt");
                File.WriteAllText(path, $"{DateTimeOffset.Now:O}\n{Environment.OSVersion}\n{error}");
                return path;
            }
            catch { }
        }
        return null;
    }
    public void ShowDashboard() => Dispatcher.BeginInvoke(RestoreDashboard);
    private void RestoreDashboard()
    {
        if (Dashboard is null || Dashboard.IsClosed) { Dashboard = new MainWindow(Runtime); MainWindow = Dashboard; }
        Dashboard.Restore();
    }
    private void TryExit()
    {
        if (Runtime.Parent.Request(Dashboard, "Выйти из Family Time и остановить учёт")) Shutdown();
    }
    private static bool SignalExisting(string name)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); return true; }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
        }
        return false;
    }
    private static bool AuthorizeMaintenance()
    {
        string path = Path.Combine(WindowsIntegration.DataDirectory, "activity.db");
        if (!File.Exists(path)) return true;
        using var store = new ActivityStore(path);
        return new ParentAccess(store, store.LoadSettings).Request(null, "Обновить или удалить Family Time");
    }
    public void ShowNotification(string title, string body)
    {
        try { tray?.ShowBalloonTip(10000, title, body, Forms.ToolTipIcon.Info); } catch { }
    }
    static int RequestShutdown()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(@"Local\FamilyTime." + WindowsIntegration.Identity);
            using var signal = EventWaitHandle.OpenExisting(@"Local\FamilyTime.Exit." + WindowsIntegration.Identity);
            signal.Set();
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(120))) return 1; }
            catch (AbandonedMutexException) { }
            mutex.ReleaseMutex(); return 0;
        }
        catch (WaitHandleCannotBeOpenedException) { return AuthorizeMaintenance() ? 0 : 1; }
        catch { return 1; }
    }
    private async Task CheckStartup()
    {
        try
        {
            Runtime = new AppRuntime(startupCheckDirectory, integrateWindows: false);
            Dashboard = new MainWindow(Runtime); MainWindow = Dashboard;
            Dashboard.Restore(); await Task.Delay(250);
            var settings = Dashboard.OpenSettingsWindow ?? throw new Exception("First-run settings did not open.");
            settings.SaveAndContinue(); await Task.Delay(150);
            if (!settings.IsVisible || !Dashboard.IsVisible || !Runtime.Settings.SetupCompleted)
                throw new Exception("Saving first-run settings hid a window or did not start accounting.");
            await Task.Run(() => Runtime.Tick());
            await Task.Delay(1100); await Task.Run(() => Runtime.Tick());
            if (Runtime.LastObservation is null) throw new Exception("Accounting did not start after saving settings.");
            string previews = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ui-checks"));
            UiCheck.Capture(Dashboard, Path.Combine(previews, "dashboard.png"));
            await settings.CaptureChecks(previews);
            settings.Close(); Dashboard.Close(); await Task.Delay(150);
            if (Dashboard.IsVisible) throw new Exception("Closing the dashboard did not hide it.");
            // Use the same event dispatch path as a second process launching the shortcut.
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FamilyTime.Check." + Guid.NewGuid().ToString("N"));
            var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => ShowDashboard(), null, Timeout.Infinite, true);
            try { signal.Set(); await Task.Delay(300); }
            finally { registration.Unregister(null); }
            if (!Dashboard.IsVisible || Dashboard.IsClosed) throw new Exception("Reopening the dashboard failed.");
            Dashboard.OpenSettings(); await Task.Delay(100);
            settings = Dashboard.OpenSettingsWindow ?? throw new Exception("Settings cannot be reopened after setup.");
            settings.SaveAndContinue(); settings.Close();
            string hash = ParentPassword.Create("parent-check-123");
            Runtime.UpdateSettings(c => c with { ParentPasswordHash = hash });
            if (!Runtime.Parent.Required || Runtime.Parent.Check("wrong") is null || Runtime.Parent.Check("parent-check-123") is not null)
                throw new Exception("Parent password verification failed.");
            // Loading the prompt also checks its help control and layout without granting access.
            var prompt = new PasswordPrompt("Проверка родительской защиты", Runtime.Parent.Check);
            prompt.Show(); prompt.Close();
            if (Runtime.Store.LoadSettings().ParentPasswordHash != hash) throw new Exception("Password was not persisted.");
            for (int attempt = 0; attempt < 5; attempt++) Runtime.Parent.Check("wrong");
            var reloadedGuard = new ParentAccess(Runtime.Store, Runtime.Store.LoadSettings);
            if (reloadedGuard.Check("parent-check-123") is null) throw new Exception("Password retry delay was not persisted.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-check.txt"),
                "PASS: first-run save, settings reopen, hide and event-driven restore, password verification and persistence. No Telegram required.");
            Shutdown(0);
        }
        catch (Exception ex) { WriteStartupError(ex, Path.GetTempPath()); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (Dashboard is not null) Dashboard.AllowClose = true;
        showRegistration?.Unregister(null); showEvent?.Dispose(); exitRegistration?.Unregister(null); exitEvent?.Dispose(); Runtime?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        if (startupCheckDirectory is not null)
        {
            try { Directory.Delete(startupCheckDirectory, true); } catch { }
        }
        if (ownsSingleton) singleton?.ReleaseMutex(); singleton?.Dispose(); base.OnExit(e);
    }
}
