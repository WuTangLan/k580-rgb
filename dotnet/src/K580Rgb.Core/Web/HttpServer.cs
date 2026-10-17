// Minimal HTTP/1.1 server for the local UI: loopback only, keep-alive, fixed-length bodies and
// server-sent events. The port is bound exclusively, so a second copy of the app fails to start
// (like the Python app) instead of sharing the port.
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace K580Rgb.Core.Web;

public sealed class HttpContext(string method, string path, Dictionary<string, string> headers, byte[] body, Stream stream)
{
    public string Method { get; } = method;
    public string Path { get; } = path;
    public IReadOnlyDictionary<string, string> Headers { get; } = headers;
    public byte[] Body { get; } = body;
    internal bool Close { get; private set; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public async Task SendAsync(int status, byte[] body, string contentType = "application/json", bool close = false)
    {
        Close |= close;
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                   $"Cache-Control: no-store\r\n{(Close ? "Connection: close\r\n" : "")}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    /// <summary>Starts a text/event-stream response; the connection closes when the handler returns.</summary>
    public async Task<Stream> BeginEventStreamAsync()
    {
        Close = true;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        await stream.FlushAsync();
        return stream;
    }

    static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large",
        _ => "Error",
    };
}

public sealed class HttpServer : IDisposable
{
    const int MaxHeader = 32 * 1024;
    const int MaxBody = 26 * 1024 * 1024;          // media uploads are limited to 25 MB
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    readonly TcpListener _listener;
    readonly Func<HttpContext, Task> _handler;
    readonly CancellationTokenSource _stop = new();

    /// <summary>Binds right away; throws SocketException when the port is taken.</summary>
    public HttpServer(int port, Func<HttpContext, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        _listener.Start();
    }

    public void Start() => _ = AcceptLoop();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        var buf = new byte[MaxHeader];
        int filled = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // read up to the end of the headers
                int end;
                while ((end = IndexOfHeaderEnd(buf, filled)) < 0)
                {
                    if (filled == buf.Length)
                        return;                    // header too large
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    idle.CancelAfter(IdleTimeout);
                    int n = await stream.ReadAsync(buf.AsMemory(filled), idle.Token);
                    if (n == 0)
                        return;
                    filled += n;
                }
                var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 3)
                    return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
                int bodyStart = end + 4;
                var ctx0 = new HttpContext(parts[0], parts[1], headers, [], stream);
                if (length < 0 || length > MaxBody)
                {
                    await ctx0.SendAsync(413, """{"error": "file larger than 25 MB"}"""u8.ToArray(), close: true);
                    return;
                }
                var body = new byte[length];
                int have = Math.Min(length, filled - bodyStart);
                Array.Copy(buf, bodyStart, body, 0, have);
                if (have < length)
                    await stream.ReadExactlyAsync(body.AsMemory(have), _stop.Token);
                // keep any pipelined bytes after this request
                int used = bodyStart + have;
                Array.Copy(buf, used, buf, 0, filled - used);
                filled -= used;

                var ctx = new HttpContext(parts[0], parts[1], headers, body, stream);
                await _handler(ctx);
                if (ctx.Close || string.Equals(ctx.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // client went away (a closed window or tab): not an error
        }
        catch (Exception e)
        {
            Log.Error("http: request failed", e);
        }
    }

    static int IndexOfHeaderEnd(byte[] buf, int filled)
    {
        for (int i = 0; i + 3 < filled; i++)
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        return -1;
    }
}
