using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using FamilyTime.Core;

// Chrome/Edge only. stdout is exclusively the framed native-messaging protocol.
if (args.Length == 0 || args[0] != $"chrome-extension://{ExtensionIdentity.Id}/") return;
var sid = WindowsIdentity.GetCurrent().User?.Value.Replace('-', '_');
if (sid is null) return;
var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
NamedPipeClientStream? pipe = null;
try
{
    var header = new byte[4];
    while (true)
    {
        await input.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > 8192) break;
        var message = new byte[length]; await input.ReadExactlyAsync(message);
        var state = JsonSerializer.Deserialize<BrowserState>(message, Wire.Json);
        if (state?.Version != Wire.Version || state.Browser is not ("chrome" or "msedge") || !Guid.TryParse(state.InstanceId, out _)) break;
        bool connected = false;
        try
        {
            if (pipe is null || !pipe.IsConnected)
            {
                pipe?.Dispose(); pipe = new NamedPipeClientStream(".", "FamilyTime.Activity." + sid, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await pipe.ConnectAsync(timeout.Token);
            }
            await pipe.WriteAsync(header); await pipe.WriteAsync(message); await pipe.FlushAsync(); connected = true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        { pipe?.Dispose(); pipe = null; }
        var answer = JsonSerializer.SerializeToUtf8Bytes(new { connected, version = Wire.Version });
        BinaryPrimitives.WriteInt32LittleEndian(header, answer.Length);
        await output.WriteAsync(header); await output.WriteAsync(answer); await output.FlushAsync();
    }
}
catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
finally { pipe?.Dispose(); }
