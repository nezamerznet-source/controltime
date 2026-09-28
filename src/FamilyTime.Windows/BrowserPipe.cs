using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FamilyTime.Windows;

public sealed class BrowserPipe(BrowserRegistry registry)
{
    public async Task Run(CancellationToken ct)
    {
        var clients = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(WindowsIntegration.PipeName, PipeDirection.In, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(Read(server, ct)); server = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; } }
            finally { server?.Dispose(); }
        }
        await Task.WhenAll(clients);
    }
    async Task Read(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var buffer = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    await pipe.ReadExactlyAsync(buffer, ct); int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer);
                    if (size is < 2 or > 8192) return;
                    var bytes = new byte[size]; await pipe.ReadExactlyAsync(bytes, ct);
                    var state = JsonSerializer.Deserialize<BrowserState>(bytes, Wire.Json);
                    if (state is not null) registry.Accept(state, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        }
    }
}
