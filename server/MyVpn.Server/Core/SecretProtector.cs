using System.Security.Cryptography;
using System.Text;

namespace MyVpn.Server.Core;

/// <summary>
/// Protects secrets (WireGuard private keys) at rest using Windows DPAPI with machine scope,
/// so that reading the configuration files does not reveal usable keys. Values are tagged with
/// a prefix so legacy plaintext values keep working and are migrated on the next save.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi.v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyVpn.Secret.v1");

    public static bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
            return plaintext ?? string.Empty;

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // Wrong machine / corrupted value: treat as missing so the caller can regenerate.
            return string.Empty;
        }
    }
}
