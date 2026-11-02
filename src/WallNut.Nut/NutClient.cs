using System.Net.Sockets;
using System.Text;

namespace WallNut.Nut;

/// <summary>A UPS listed by the server, from LIST UPS.</summary>
public sealed record UpsInfo(string Name, string Description);

/// <summary>
/// One TCP connection to one NUT server (upsd). Commands are sent one at a time; the client
/// is safe to share between threads. It does not reconnect by itself: the caller decides
/// when to reconnect, so connection loss is always visible.
/// </summary>
public sealed class NutClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _replyTimeout;
    private bool _disposed;
    private bool _broken;

    public string Host { get; }
    public int Port { get; }

    /// <summary>False once the connection was closed, failed, or got out of step.</summary>
    public bool IsConnected => !_disposed && !_broken && _tcp.Connected;

    private NutClient(TcpClient tcp, string host, int port, TimeSpan replyTimeout)
    {
        _tcp = tcp;
        Host = host;
        Port = port;
        _replyTimeout = replyTimeout;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
    }

    /// <summary>Opens a connection. Throws <see cref="SocketException"/> or <see cref="TimeoutException"/> on failure.</summary>
    public static async Task<NutClient> ConnectAsync(
        string host, int port = NutProtocol.DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            await tcp.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Connecting to {host}:{port} timed out after {limit.TotalSeconds:0}s.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        return new NutClient(tcp, host, port, limit);
    }

    // ---- Queries -------------------------------------------------------------------------

    /// <summary>Lists the UPS devices this server manages.</summary>
    public async Task<IReadOnlyList<UpsInfo>> ListUpsAsync(CancellationToken ct = default)
    {
        var lines = await SendListAsync("LIST UPS", ct);
        // UPS <name> "<description>"
        return lines.Select(NutProtocol.Tokenize)
            .Where(t => t.Count >= 2 && t[0] == "UPS")
            .Select(t => new UpsInfo(t[1], t.Count > 2 ? t[2] : ""))
            .ToList();
    }

    /// <summary>Gets every variable for a UPS (battery.charge, ups.status, input.voltage, ...).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ListVarsAsync(string ups, CancellationToken ct = default)
    {
        NutProtocol.RequireName(ups, nameof(ups));
        var lines = await SendListAsync($"LIST VAR {ups}", ct);
        // VAR <ups> <name> "<value>"
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in lines.Select(NutProtocol.Tokenize))
        {
            if (t.Count >= 4 && t[0] == "VAR") vars[t[2]] = t[3];
        }
        return vars;
    }

    /// <summary>Gets one variable's value.</summary>
    public async Task<string> GetVarAsync(string ups, string variable, CancellationToken ct = default)
    {
        NutProtocol.RequireName(ups, nameof(ups));
        NutProtocol.RequireName(variable, nameof(variable));
        var reply = await SendAsync($"GET VAR {ups} {variable}", ct);
        // VAR <ups> <name> "<value>"
        var t = NutProtocol.Tokenize(reply);
        if (t.Count < 4 || t[0] != "VAR")
            throw new FormatException($"Unexpected reply to GET VAR: {reply}");
        return t[3];
    }

    /// <summary>Gets and parses ups.status.</summary>
    public async Task<UpsStatus> GetStatusAsync(string ups, CancellationToken ct = default) =>
        UpsStatus.Parse(await GetVarAsync(ups, "ups.status", ct));

    // ---- Session -------------------------------------------------------------------------

    /// <summary>Sends USERNAME and PASSWORD. Needed before <see cref="LoginAsync"/>, and on servers that require it for reads.</summary>
    public async Task AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        NutProtocol.RequireName(username, nameof(username));
        ArgumentNullException.ThrowIfNull(password);
        await SendExpectOkAsync($"USERNAME {username}", ct);
        await SendExpectOkAsync($"PASSWORD {NutProtocol.Quote(password)}", ct, logAs: "PASSWORD ****");
    }

    /// <summary>
    /// Tells the server this machine is powered by the UPS (what upsmon does as a "secondary").
    /// The server then waits for this client to disconnect before powering off during a shutdown.
    /// </summary>
    public Task LoginAsync(string ups, CancellationToken ct = default) =>
        SendExpectOkAsync($"LOGIN {NutProtocol.RequireName(ups, nameof(ups))}", ct);

    /// <summary>Ends the session politely, then closes the connection.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try { await SendAsync("LOGOUT", ct); }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or NutException) { }
        await DisposeAsync();
    }

    // ---- Wire ----------------------------------------------------------------------------

    private async Task SendExpectOkAsync(string command, CancellationToken ct, string? logAs = null)
    {
        var reply = await SendAsync(command, ct, logAs);
        if (!reply.StartsWith("OK", StringComparison.Ordinal))
            throw new FormatException($"Unexpected reply to {logAs ?? command}: {reply}");
    }

    /// <summary>Sends a command and returns its single reply line.</summary>
    private async Task<string> SendAsync(string command, CancellationToken ct, string? logAs = null)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteLineAsync(command, ct);
            return CheckError(await ReadLineAsync(ct), logAs ?? command);
        }
        catch (Exception e) when (IsTransportFailure(e)) { Break(); throw; }
        finally { _lock.Release(); }
    }

    /// <summary>Sends a LIST command and returns the lines between BEGIN LIST and END LIST.</summary>
    private async Task<List<string>> SendListAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteLineAsync(command, ct);
            var first = CheckError(await ReadLineAsync(ct), command);
            var query = command["LIST ".Length..];
            if (first != $"BEGIN LIST {query}")
                throw new FormatException($"Unexpected reply to {command}: {first}");

            var lines = new List<string>();
            while (true)
            {
                var line = CheckError(await ReadLineAsync(ct), command);
                if (line == $"END LIST {query}") return lines;
                lines.Add(line);
            }
        }
        catch (Exception e) when (IsTransportFailure(e)) { Break(); throw; }
        finally { _lock.Release(); }
    }

    // A timeout, cancellation or broken socket mid-command leaves the reply stream out of
    // step, so the connection can't be trusted again. Close it; the caller reconnects.
    private static bool IsTransportFailure(Exception e) =>
        e is IOException or SocketException or TimeoutException or OperationCanceledException or FormatException;

    private void Break()
    {
        _broken = true;
        _tcp.Dispose();
    }

    private static string CheckError(string reply, string command)
    {
        if (reply.StartsWith("ERR ", StringComparison.Ordinal))
            throw new NutException(reply[4..].Split(' ')[0], command);
        return reply;
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_broken) throw new IOException($"The connection to {Host}:{Port} is broken; reconnect.");
        await _writer.WriteLineAsync(line.AsMemory(), ct);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_replyTimeout);
        string? line;
        try { line = await _reader.ReadLineAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No reply from {Host}:{Port} within {_replyTimeout.TotalSeconds:0}s.");
        }
        return line ?? throw new IOException($"{Host}:{Port} closed the connection.");
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _reader.Dispose();
        _writer.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
