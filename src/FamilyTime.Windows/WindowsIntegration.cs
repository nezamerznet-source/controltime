using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FamilyTime.Windows;

public static class WindowsIntegration
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FamilyTime");
    public static string Identity => WindowsIdentity.GetCurrent().User?.Value.Replace('-', '_') ?? throw new InvalidOperationException("Не удалось определить учётную запись Windows.");
    public static string PipeName => "FamilyTime.Activity." + Identity;
    public static void AutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("FamilyTime", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("FamilyTime", false);
    }
    public static void RegisterBrowser()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(AppContext.BaseDirectory, "FamilyTime.NativeHost.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Компонент браузера не найден. Переустановите полную сборку.");
        var manifest = new { name = Wire.HostName, description = "Family Time — local activity bridge", path, type = "stdio", allowed_origins = new[] { $"chrome-extension://{ExtensionIdentity.Id}/" } };
        var manifestPath = Path.Combine(DataDirectory, "native-host.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
        foreach (var vendor in new[] { @"Google\Chrome", @"Microsoft\Edge" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\" + vendor + @"\NativeMessagingHosts\" + Wire.HostName);
            key.SetValue("", manifestPath);
        }
    }
    public static void Unregister()
    {
        AutoStart(false);
        foreach (var vendor in new[] { @"Google\Chrome", @"Microsoft\Edge" })
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + vendor + @"\NativeMessagingHosts\" + Wire.HostName, false);
    }
    public static void OpenHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    public static void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
}

public static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Pointer; }
    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    static byte[] Transform(byte[] data, bool protect)
    {
        var source = new Blob { Length = data.Length, Pointer = Marshal.AllocHGlobal(data.Length) }; Blob result = default;
        try
        {
            Marshal.Copy(data, 0, source.Pointer, data.Length);
            bool ok = protect ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok) throw new IOException("Не удалось защитить/прочитать токен в этой учётной записи Windows.");
            var bytes = new byte[result.Length]; Marshal.Copy(result.Pointer, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            for (var i = 0; i < data.Length; i++) Marshal.WriteByte(source.Pointer, i, 0);
            Marshal.FreeHGlobal(source.Pointer); if (result.Pointer != IntPtr.Zero) LocalFree(result.Pointer);
            Array.Clear(data);
        }
    }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
}
