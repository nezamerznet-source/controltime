using System.Text.Json;

namespace FamilyTime.Windows;

public static class Branding
{
    private sealed record Links(string SupportUrl = "", string RepositoryUrl = "");
    private static readonly Links links = Load();
    private static Links Load()
    {
        try { return JsonSerializer.Deserialize<Links>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "branding.json")), Wire.Json) ?? new(); }
        catch { return new(); }
    }
    public static bool Valid(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0;
    public static string Support(AppSettings c) => c.SupportUrl.Length > 0 ? c.SupportUrl : links.SupportUrl;
    public static string Repository(AppSettings c) => c.RepositoryUrl.Length > 0 ? c.RepositoryUrl : links.RepositoryUrl;
}
