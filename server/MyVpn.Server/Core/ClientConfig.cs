using System.Text;

namespace MyVpn.Server.Core;

public static class ClientConfig
{
    /// <summary>
    /// Marker written in place of the client private key when the server does not know it.
    /// The Android client replaces this token with its own locally generated private key.
    /// </summary>
    public const string PrivateKeyPlaceholder = "__CLIENT_PRIVATE_KEY__";

    /// <summary>Builds a full-tunnel WireGuard configuration for a peer.</summary>
    public static string Build(ServerOptions server, Peer peer)
    {
        var privateKey = string.IsNullOrWhiteSpace(peer.PrivateKey)
            ? PrivateKeyPlaceholder
            : peer.PrivateKey;

        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {privateKey}");
        sb.AppendLine($"Address = {peer.TunnelAddress}/32");
        if (!string.IsNullOrWhiteSpace(server.Dns))
            sb.AppendLine($"DNS = {server.Dns}");
        sb.AppendLine();
        sb.AppendLine("[Peer]");
        sb.AppendLine($"PublicKey = {server.PublicKey}");
        if (!string.IsNullOrWhiteSpace(peer.PresharedKey))
            sb.AppendLine($"PresharedKey = {peer.PresharedKey}");
        sb.AppendLine("AllowedIPs = 0.0.0.0/0, ::/0");
        sb.AppendLine($"Endpoint = {server.PublicEndpoint}");
        sb.AppendLine("PersistentKeepalive = 25");
        return sb.ToString();
    }
}
