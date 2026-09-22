using System.Diagnostics;
using MyVpn.Server.Core;
using MyVpn.Server.Services;

namespace MyVpn.Server.UI;

/// <summary>Shows the generated client configuration for a peer, including a scannable QR code.</summary>
public sealed class PeerDetailsDialog : Form
{
    private readonly string _config;
    private readonly string _peerName;
    private System.Windows.Forms.Timer? _clipboardTimer;

    public PeerDetailsDialog(ServerOptions server, Peer peer)
    {
        _peerName = string.IsNullOrWhiteSpace(peer.Name) ? "peer" : peer.Name;
        _config = ClientConfig.Build(server, peer);

        Text = $"Peer: {_peerName}";
        ClientSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var picture = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = 340,
            SizeMode = PictureBoxSizeMode.Zoom,
            Padding = new Padding(12),
            BackColor = Color.White,
        };
        try
        {
            using var stream = new MemoryStream(QrGenerator.Png(_config));
            picture.Image = Image.FromStream(stream);
        }
        catch (Exception ex)
        {
            picture.Controls.Add(new Label { Text = "QR unavailable: " + ex.Message, Dock = DockStyle.Fill });
        }

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            Text = _config.Replace("\n", "\r\n"),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
        };

        var copy = new Button { Text = "Copy config", AutoSize = true };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(_config);
            copy.Text = "Copied!";
            ScheduleClipboardClear();
        };

        var save = new Button { Text = "Save .conf", AutoSize = true };
        save.Click += (_, _) => SaveConfig();

        var openFolder = new Button { Text = "Open folder", AutoSize = true };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(ConfigStore.PeersDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", ConfigStore.PeersDirectory) { UseShellExecute = true });
        };

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };

        var warning = new Label
        {
            Text = string.IsNullOrWhiteSpace(peer.PrivateKey)
                ? "No client private key is stored; the app fills it in on import."
                : "Contains a private key - keep secret.",
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(6, 9, 12, 0),
        };

        buttons.Controls.Add(warning);
        buttons.Controls.AddRange(new Control[] { copy, save, openFolder, close });

        Controls.Add(text);
        Controls.Add(picture);
        Controls.Add(buttons);
    }

    private void ScheduleClipboardClear()
    {
        _clipboardTimer?.Stop();
        _clipboardTimer?.Dispose();

        _clipboardTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        _clipboardTimer.Tick += (_, _) =>
        {
            _clipboardTimer?.Stop();
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == _config)
                    Clipboard.Clear();
            }
            catch
            {
                // Another process may hold the clipboard; ignore.
            }
        };
        _clipboardTimer.Start();
    }

    private void SaveConfig()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = _peerName + ".conf",
            Filter = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(ConfigStore.PeersDirectory)
                ? ConfigStore.PeersDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, _config);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _clipboardTimer?.Stop();
        _clipboardTimer?.Dispose();
        _clipboardTimer = null;
        base.OnFormClosed(e);
    }
}
