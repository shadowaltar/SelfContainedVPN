using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MyVpn.Server.Services;

using MyVpn.Server.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyVpn");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string PeersDirectory => Path.Combine(DataDirectory, "peers");

    /// <summary>Set when the data directory could not be locked down; surfaced in the UI log.</summary>
    public static string? LastSecurityWarning { get; private set; }

    public AppConfig Load()
    {
        HardenDataDirectory();

        AppConfig config;
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var backup = ConfigPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                try { File.Move(ConfigPath, backup, overwrite: true); } catch { /* ignore */ }
                LastSecurityWarning = $"config.json could not be read ({ex.Message}); it was moved aside and a fresh configuration was created.";
                config = new AppConfig();
            }
        }
        else
        {
            config = new AppConfig();
        }

        // Defend against a JSON file with "Server": null or "Peers": null.
        config.Server ??= new ServerOptions();
        config.Peers ??= new List<Peer>();

        // Secrets are stored DPAPI-protected; decrypt into memory (legacy plaintext passes through).
        var needsMigration = false;
        if (!SecretProtector.IsProtected(config.Server.PrivateKey))
            needsMigration = true;
        foreach (var peer in config.Peers)
        {
            if (!SecretProtector.IsProtected(peer.PrivateKey)
                || (!string.IsNullOrEmpty(peer.PresharedKey) && !SecretProtector.IsProtected(peer.PresharedKey)))
                needsMigration = true;

            peer.PrivateKey = SecretProtector.Unprotect(peer.PrivateKey);
            if (!string.IsNullOrEmpty(peer.PresharedKey))
                peer.PresharedKey = SecretProtector.Unprotect(peer.PresharedKey);
        }

        if (!WgKeys.IsValidKey(config.Server.PrivateKey))
        {
            var (privateKey, publicKey) = WgKeys.Generate();
            config.Server.PrivateKey = privateKey;
            config.Server.PublicKey = publicKey;
            Save(config);
        }
        else if (string.IsNullOrEmpty(config.Server.PublicKey))
        {
            config.Server.PublicKey = WgKeys.PublicFromPrivate(config.Server.PrivateKey);
            Save(config);
        }
        else if (needsMigration)
        {
            // Re-encrypt a legacy plaintext file.
            Save(config);
        }

        return config;
    }

    public void Save(AppConfig config)
    {
        HardenDataDirectory();

        var node = JsonSerializer.SerializeToNode(config, JsonOptions)?.AsObject() ?? new JsonObject();

        if (node["Server"] is JsonObject server)
            server["PrivateKey"] = SecretProtector.Protect(config.Server.PrivateKey);

        if (node["Peers"] is JsonArray peers)
        {
            for (var i = 0; i < peers.Count && i < config.Peers.Count; i++)
            {
                if (peers[i] is not JsonObject peer)
                    continue;

                peer["PrivateKey"] = SecretProtector.Protect(config.Peers[i].PrivateKey);
                if (peer["PresharedKey"] is not null)
                    peer["PresharedKey"] = SecretProtector.Protect(config.Peers[i].PresharedKey);
            }
        }

        File.WriteAllText(ConfigPath, node.ToJsonString(JsonOptions));
        RestrictFile(ConfigPath);
    }

    public void SavePeerConfig(string peerName, string contents)
    {
        Directory.CreateDirectory(PeersDirectory);
        RestrictPath(PeersDirectory, isDirectory: true);

        var safe = string.Concat(peerName.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe)) safe = "peer";
        var path = Path.Combine(PeersDirectory, safe + ".conf");

        File.WriteAllText(path, contents);
        RestrictFile(path);
    }

    /// <summary>
    /// Restricts the data directory (and its contents) to SYSTEM and Administrators so that other
    /// local users cannot read the private keys.
    /// </summary>
    private static void HardenDataDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Directory.CreateDirectory(DataDirectory);
            RestrictPath(DataDirectory, isDirectory: true);

            foreach (var file in Directory.EnumerateFiles(DataDirectory))
                RestrictPath(file, isDirectory: false);

            if (Directory.Exists(PeersDirectory))
            {
                RestrictPath(PeersDirectory, isDirectory: true);
                foreach (var file in Directory.EnumerateFiles(PeersDirectory))
                    RestrictPath(file, isDirectory: false);
            }

            LastSecurityWarning = null;
        }
        catch (Exception ex)
        {
            LastSecurityWarning = $"Could not restrict permissions on '{DataDirectory}': {ex.Message}";
        }
    }

    private static void RestrictFile(string path)
    {
        try { RestrictPath(path, isDirectory: false); }
        catch { /* best effort */ }
    }

    private static void RestrictPath(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;

        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleAll(rule);

        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));

        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
}
