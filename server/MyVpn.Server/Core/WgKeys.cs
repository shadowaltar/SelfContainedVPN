using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace MyVpn.Server.Core;

/// <summary>
/// Curve25519 key pair helpers for WireGuard. WireGuard private keys are 32 random bytes
/// clamped exactly like X25519 scalars, and the public key is the X25519 base-point multiple.
/// </summary>
public static class WgKeys
{
    public static (string PrivateKey, string PublicKey) Generate()
    {
        var rng = new SecureRandom();
        var priv = new byte[32];
        rng.NextBytes(priv);
        Clamp(priv);
        return (Convert.ToBase64String(priv), Convert.ToBase64String(PublicFromPrivate(priv)));
    }

    public static string PublicFromPrivate(string privateKeyBase64)
        => Convert.ToBase64String(PublicFromPrivate(Convert.FromBase64String(privateKeyBase64)));

    public static bool IsValidKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            return Convert.FromBase64String(base64).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] PublicFromPrivate(byte[] privateKey)
    {
        var parameters = new X25519PrivateKeyParameters(privateKey, 0);
        return parameters.GeneratePublicKey().GetEncoded();
    }

    private static void Clamp(byte[] key)
    {
        key[0] &= 248;
        key[31] &= 127;
        key[31] |= 64;
    }
}
