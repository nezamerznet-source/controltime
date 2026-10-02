using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FamilyTime.Windows;

internal static class UiCheck
{
    internal sealed class TelegramApi : ITelegramApi
    {
        internal List<string> Reports { get; } = [];
        public Task<System.Text.Json.JsonElement[]> Poll(string token, long offset, CancellationToken ct) =>
            Task.FromResult(Array.Empty<System.Text.Json.JsonElement>());
        public Task<string> Username(string token, CancellationToken ct) => Task.FromResult("ui_check");
        public Task<long> Send(string token, long chat, string body, bool keyboard, CancellationToken ct)
        {
            Reports.Add(body); return Task.FromResult(1L);
        }
    }
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path); encoder.Save(output);
    }
}
