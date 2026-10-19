using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using EspacoLeve.Core;

namespace EspacoLeve.App;

internal sealed class SingleInstance : IDisposable
{
    readonly FileStream? lease;
    readonly CancellationTokenSource stop = new();
    readonly string pipe = "espaco-leve-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlatformContext.Current.Home)))[..20];
    public bool IsOwner => lease is not null;
    public static event Action? OpenRequested;
    public SingleInstance()
    {
        var directory = PlatformContext.Current.StateDirectory;
        Directory.CreateDirectory(directory);
        try { lease = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        _ = Listen();
    }
    public void Notify()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            client.WriteByte(1);
        }
        catch (TimeoutException) { }
        catch (IOException) { }
    }
    async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token);
                var data = new byte[1];
                if (await server.ReadAsync(data, stop.Token) > 0) OpenRequested?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { if (!stop.IsCancellationRequested) await Task.Delay(500, stop.Token); }
        }
    }
    public void Dispose() { stop.Cancel(); lease?.Dispose(); stop.Dispose(); }
}
