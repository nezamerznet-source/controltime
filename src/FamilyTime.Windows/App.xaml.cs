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
    public AppRuntime Runtime { get; private set; } = null!;
    public MainWindow Dashboard { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--register")) { try { WindowsIntegration.RegisterBrowser(); Shutdown(0); } catch { Shutdown(1); } return; }
        if (e.Args.Contains("--unregister")) { try { WindowsIntegration.Unregister(); Shutdown(0); } catch { Shutdown(1); } return; }
        if (e.Args.Contains("--shutdown")) { Shutdown(RequestShutdown()); return; }
        if (e.Args.Contains("--startup-check"))
        {
            startupCheckDirectory = Path.Combine(Path.GetTempPath(), "FamilyTime-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                Runtime = new AppRuntime(startupCheckDirectory);
                Dashboard = new MainWindow(Runtime); MainWindow = Dashboard;
                using var testTray = new Forms.NotifyIcon { Text = "Family Time", Icon = SystemIcons.Application };
                // Load both XAML trees without starting monitoring, Telegram or changing autostart.
                var settingsWindow = new SettingsWindow(Runtime);
                Shutdown(0);
            }
            catch (Exception ex) { WriteStartupError(ex, Path.GetTempPath()); Shutdown(1); }
            return;
        }
        singleton = new Mutex(true, @"Local\FamilyTime." + WindowsIntegration.Identity, out bool first);
        ownsSingleton = first;
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\FamilyTime.Show." + WindowsIntegration.Identity); signal.Set(); } catch { }
            Shutdown(); return;
        }
        try
        {
            Runtime = new AppRuntime();
            Dashboard = new MainWindow(Runtime); MainWindow = Dashboard;
            tray = new Forms.NotifyIcon { Text = "Family Time — семейный учёт времени", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть статистику", null, (_, _) => ShowDashboard());
            menu.Items.Add("Пауза / продолжить учёт", null, (_, _) => Runtime.TogglePause());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => ShowDashboard();
            Runtime.Notify += (title, body) => Dispatcher.BeginInvoke(() => ShowNotification(title, body));
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FamilyTime.Show." + WindowsIntegration.Identity);
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ShowDashboard(), null, Timeout.Infinite, false);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FamilyTime.Exit." + WindowsIntegration.Identity);
            exitRegistration = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, false);
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
    public void ShowDashboard() => Dispatcher.BeginInvoke(() => { Dashboard.Show(); Dashboard.WindowState = WindowState.Normal; Dashboard.Activate(); });
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
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(20))) return 1; }
            catch (AbandonedMutexException) { }
            mutex.ReleaseMutex(); return 0;
        }
        catch (WaitHandleCannotBeOpenedException) { return 0; }
        catch { return 1; }
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
