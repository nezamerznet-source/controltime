using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FamilyTime.Windows;

public sealed class WindowsProbe : IDisposable
{
    private volatile bool locked;
    private volatile bool suspended;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private DateTimeOffset controllerInput = DateTimeOffset.MinValue;
    private readonly XInputState[] controllers = new XInputState[4];
    private readonly Dictionary<uint, (string Key, string Name, DateTimeOffset At)> processes = new();
    public event Action? BoundaryChanged;
    public WindowsProbe()
    {
        IntPtr desktop = Native.OpenInputDesktop(0, false, 0x0001);
        locked = desktop == IntPtr.Zero;
        if (desktop != IntPtr.Zero) Native.CloseDesktop(desktop);
        SystemEvents.SessionSwitch += OnSession; SystemEvents.PowerModeChanged += OnPower;
    }
    void OnSession(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff) locked = true;
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect or SessionSwitchReason.SessionLogon) locked = false;
        BoundaryChanged?.Invoke();
    }
    void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) suspended = true;
        if (e.Mode == PowerModes.Resume) suspended = false;
        BoundaryChanged?.Invoke();
    }
    public Observation Read(AppSettings c, bool paused, BrowserRegistry browser)
    {
        var now = DateTimeOffset.UtcNow;
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var last = Native.GetLastInputInfo(ref input) ? now.AddMilliseconds(-unchecked((uint)Environment.TickCount64 - input.Tick)) : now.AddHours(-24);
        ReadController(now);
        if (controllerInput > last) last = controllerInput;
        var handle = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(handle, out var pid);
        string key = "", name = "";
        if (handle != IntPtr.Zero && pid != 0 && !locked && !suspended)
        {
            if (processes.TryGetValue(pid, out var saved) && now - saved.At < TimeSpan.FromSeconds(15)) { key = saved.Key; name = saved.Name; }
            else
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid); key = process.ProcessName.ToLowerInvariant(); name = process.ProcessName;
                    try { var description = process.MainModule?.FileVersionInfo.FileDescription; if (!string.IsNullOrWhiteSpace(description)) name = description; } catch { }
                    name = new string(name.Where(ch => !char.IsControl(ch)).Take(100).ToArray());
                    processes[pid] = (key, name, now);
                    if (processes.Count > 128) foreach (var p in processes.Where(x => now - x.Value.At > TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray()) processes.Remove(p);
                }
                catch { key = "unknown-app"; name = "Неизвестное приложение"; }
            }
        }
        var web = browser.Resolve(key, now);
        return new(now, clock.Elapsed.TotalSeconds, last, locked || suspended, paused,
            key, name, web.Domain, Categories.Resolve(key, web.Domain, c), web.Foreground, web.Background,
            Categories.IsBrowser(key) && !web.Connected);
    }
    void ReadController(DateTimeOffset now)
    {
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (Native.XInputGetState(i, out var state) != 0) continue;
                var old = controllers[i].Pad; var p = state.Pad;
                bool moved = Math.Abs((int)p.ThumbLX - old.ThumbLX) > 5000 || Math.Abs((int)p.ThumbLY - old.ThumbLY) > 5000 ||
                    Math.Abs((int)p.ThumbRX - old.ThumbRX) > 5000 || Math.Abs((int)p.ThumbRY - old.ThumbRY) > 5000;
                // Holding a stick (e.g. moving forward) is input even when its position is unchanged.
                // A conservative dead zone excludes small analogue drift.
                bool heldStick = (long)p.ThumbLX * p.ThumbLX + (long)p.ThumbLY * p.ThumbLY > 9000L * 9000 ||
                    (long)p.ThumbRX * p.ThumbRX + (long)p.ThumbRY * p.ThumbRY > 9000L * 9000;
                if (p.Buttons != 0 || p.LeftTrigger > 40 || p.RightTrigger > 40 || moved || heldStick) controllerInput = now;
                controllers[i] = state;
            }
        }
        catch (DllNotFoundException) { }
    }
    public void Dispose() { SystemEvents.SessionSwitch -= OnSession; SystemEvents.PowerModeChanged -= OnPower; }
    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] struct XInputPad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; }
    [StructLayout(LayoutKind.Sequential)] struct XInputState { public uint Packet; public XInputPad Pad; }
    static class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint user, out XInputState state);
    }
}
