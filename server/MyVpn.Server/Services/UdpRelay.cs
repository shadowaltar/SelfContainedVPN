using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MyVpn.Server.Services;

/// <summary>
/// UDP relay for the Windows host. WSL's mirrored networking does not deliver packets from other
/// LAN devices to services bound inside WSL, so the host listens on the WireGuard port and forwards
/// each client's datagrams into WSL (and replies back). One upstream socket per client is used so
/// the WireGuard server learns distinct peer endpoints.
/// </summary>
internal static class UdpRelay
{
    private const string Distro = "Ubuntu";

    public static async Task RunAsync(int listenPort, int targetPort, CancellationToken token, Action<string>? log = null)
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort));
        var clients = new ConcurrentDictionary<string, Client>();
        var target = ResolveWslIp();

        log?.Invoke($"listening on 0.0.0.0:{listenPort} -> WSL {target}:{targetPort}");

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }

            var ip = target ?? ResolveWslIp();
            if (ip is null)
                continue;
            target = ip;

            var client = clients.GetOrAdd(received.RemoteEndPoint.ToString(),
                _ => new Client(listener, received.RemoteEndPoint, ip, targetPort));
            await client.ForwardAsync(received.Buffer);
        }
    }

    private static string? ResolveWslIp()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-d", Distro, "-u", "root", "--", "hostname", "-I" })
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(s => s.Count(c => c == '.') == 3);
        }
        catch
        {
            return null;
        }
    }

    private sealed class Client
    {
        private readonly UdpClient _listener;
        private readonly IPEndPoint _client;
        private readonly UdpClient? _upstream;

        public Client(UdpClient listener, IPEndPoint client, string targetIp, int targetPort)
        {
            _listener = listener;
            _client = client;

            if (IPAddress.TryParse(targetIp, out var ip))
            {
                _upstream = new UdpClient();
                _upstream.Connect(new IPEndPoint(ip, targetPort));
                _ = PumpAsync();
            }
        }

        public async Task ForwardAsync(byte[] data)
        {
            if (_upstream is null)
                return;
            try
            {
                await _upstream.SendAsync(data, data.Length);
            }
            catch
            {
                // ignore; the peer will retry
            }
        }

        private async Task PumpAsync()
        {
            if (_upstream is null)
                return;
            while (true)
            {
                byte[] data;
                try
                {
                    data = (await _upstream.ReceiveAsync()).Buffer;
                }
                catch
                {
                    break;
                }

                try
                {
                    await _listener.SendAsync(data, data.Length, _client);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
