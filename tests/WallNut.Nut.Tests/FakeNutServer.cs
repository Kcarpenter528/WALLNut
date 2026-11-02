using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WallNut.Nut.Tests;

/// <summary>
/// A tiny stand-in for upsd on 127.0.0.1. Each received command line is passed to
/// <c>respond</c>, which returns the reply lines to send back (empty = say nothing,
/// null = hang up). Every received line is recorded in <see cref="Received"/>.
/// </summary>
internal sealed class FakeNutServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, IEnumerable<string>?> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Received { get; } = [];

    public FakeNutServer(Func<string, IEnumerable<string>?> respond)
    {
        _respond = respond;
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    lock (Received) Received.Add(line);
                    var reply = _respond(line);
                    if (reply is null) return; // hang up
                    foreach (var r in reply) await writer.WriteLineAsync(r);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}
