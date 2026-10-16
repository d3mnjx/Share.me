using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ShareMe;

// Every yt-dlp HTTP request (including redirects and CDN requests) goes through this
// loopback-only proxy. Resolve once, reject non-public addresses, and connect to that
// exact IP so DNS rebinding cannot turn a validated name into an internal request.
public sealed class PublicProxy(AppSettings settings, ILogger<PublicProxy> logger) : BackgroundService
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly SemaphoreSlim slots = new(64);
    public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        listener.Start();
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                try
                {
                    var client = await listener.AcceptTcpClientAsync(stoppingToken);
                    _ = Handle(client, stoppingToken);
                }
                catch { slots.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }
    private async Task Handle(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var token = timeout.Token;
            var stream = client.GetStream();
            var started = false;
            try
            {
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, token) == 0) throw new IOException("Incomplete proxy request.");
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                if (header.Count >= 16384) throw new IOException("Proxy header too large.");
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length != 3) throw new IOException("Invalid proxy request.");
                var tunnel = request[0] == "CONNECT";
                if (!Uri.TryCreate(tunnel ? "https://" + request[1] : request[1], UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https") || uri.UserInfo != "" || uri.Port is not (80 or 443))
                    throw new IOException("Only public web destinations are supported.");
                var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, token);
                if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip))) throw new IOException("Private network destination blocked.");
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(addresses, uri.Port, token);
                var remote = upstream.GetStream();
                if (tunnel)
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                else
                {
                    if (uri.Scheme != "http") throw new IOException("HTTPS requires CONNECT.");
                    var forwarded = $"{request[0]} {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n";
                    foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                    {
                        var name = line.Split(':', 2)[0];
                        if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) forwarded += line + "\r\n";
                    }
                    await remote.WriteAsync(Encoding.ASCII.GetBytes(forwarded + "\r\n"), token);
                }
                started = true;
                var upload = stream.CopyToAsync(remote, token);
                var download = remote.CopyToAsync(stream, token);
                await Task.WhenAny(upload, download);
                await timeout.CancelAsync();
                try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                logger.LogDebug(e, "Download proxy connection ended");
                if (!started && !token.IsCancellationRequested)
                {
                    try { await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token); }
                    catch (Exception writeError) when (writeError is IOException or OperationCanceledException) { }
                }
            }
            finally { slots.Release(); }
        }
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || b[0] == 100 && b[1] is >= 64 and <= 127
                || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && (b[1] is 0 or 168 || b[1] == 88 && b[2] == 99)
                || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Accept global IPv6 unicast only, excluding transition and documentation ranges.
        return b.Length == 16 && (b[0] & 0xe0) == 0x20 && address.ScopeId == 0
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 0x02 || b[2] == 0x0d && b[3] == 0xb8))
            && !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && b[1] == 0xff);
    }
}
