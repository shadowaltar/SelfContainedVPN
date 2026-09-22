using MyVpn.Server.Core;
using MyVpn.Server.Services;
using MyVpn.Server.UI;

namespace MyVpn.Server;

public sealed class MainForm : Form
{
    private readonly ConfigStore _store = new();
    private readonly WslVpnServer _server;
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly Label _status = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _publicKey = new();
    private readonly TextBox _log = new();
    private readonly DataGridView _grid = new();

    private readonly Button _start = new() { Text = "Start server", AutoSize = true };
    private readonly Button _stop = new() { Text = "Stop server", AutoSize = true };
    private readonly Button _detect = new() { Text = "Detect", AutoSize = true };
    private readonly Button _addPeer = new() { Text = "Add peer", AutoSize = true };
    private readonly Button _togglePeer = new() { Text = "Enable / disable", AutoSize = true };
    private readonly Button _deletePeer = new() { Text = "Delete peer", AutoSize = true };
    private readonly Button _showPeer = new() { Text = "Show config / QR", AutoSize = true };

    private bool _refreshing;

    public MainForm()
    {
        _server = new WslVpnServer(_store);
        _server.Log += AppendLog;

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* ignore */ }

        BuildUi();
        LoadServerInfo();
        UpdateUiState();

        _timer.Interval = 3000;
        _timer.Tick += async (_, _) => await RefreshEverythingAsync();
        _timer.Start();
        _ = RefreshEverythingAsync();
    }

    private void BuildUi()
    {
        Text = "MyVpn Server";
        MinimumSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));

        // ---- server info ----
        var info = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _status.Text = "Stopped";
        _status.AutoSize = true;
        _status.Anchor = AnchorStyles.Left;
        _status.Font = new Font(Font, FontStyle.Bold);

        info.Controls.Add(new Label { Text = "Status:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        info.Controls.Add(_status, 1, 0);
        info.Controls.Add(_start, 2, 0);
        info.Controls.Add(_stop, 3, 0);

        info.Controls.Add(new Label { Text = "Public endpoint:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _endpoint.Dock = DockStyle.Fill;
        info.Controls.Add(_endpoint, 1, 1);
        info.Controls.Add(_detect, 2, 1);

        info.Controls.Add(new Label { Text = "Server public key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _publicKey.Dock = DockStyle.Fill;
        _publicKey.ReadOnly = true;
        _publicKey.Font = new Font("Consolas", 9f);
        info.Controls.Add(_publicKey, 1, 2);
        info.SetColumnSpan(_publicKey, 3);

        // ---- toolbar ----
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
        toolbar.Controls.AddRange(new Control[] { _addPeer, _togglePeer, _deletePeer, _showPeer });

        // ---- peers grid ----
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;
        _grid.SelectionChanged += (_, _) => UpdateUiState();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowSelectedPeer(); };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "address", HeaderText = "Tunnel IP", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "enabled", HeaderText = "State", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "online", HeaderText = "Connection", FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "download", HeaderText = "Download", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "upload", HeaderText = "Upload", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "handshake", HeaderText = "Last handshake", FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", HeaderText = "Public key", FillWeight = 170 });

        // ---- log ----
        var logPanel = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(6) };
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 8.5f);
        _log.BackColor = Color.White;
        logPanel.Controls.Add(_log);

        root.Controls.Add(info, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(_grid, 0, 2);
        root.Controls.Add(logPanel, 0, 3);
        Controls.Add(root);

        _start.Click += async (_, _) => await StartServerAsync();
        _stop.Click += async (_, _) => await StopServerAsync();
        _detect.Click += async (_, _) => await DetectIpAsync();
        _addPeer.Click += (_, _) => AddPeer();
        _togglePeer.Click += (_, _) => ToggleSelectedPeer();
        _deletePeer.Click += (_, _) => DeleteSelectedPeer();
        _showPeer.Click += (_, _) => ShowSelectedPeer();
    }

    private void LoadServerInfo()
    {
        _publicKey.Text = _server.Config.Server.PublicKey;
        _endpoint.Text = _server.Config.Server.PublicEndpoint;
        AppendLog($"Backend: {_server.BackendName} (WireGuard server runs in WSL)");
        if (ConfigStore.LastSecurityWarning is { } warning)
            AppendLog("SECURITY: " + warning);
        RefreshGridRows();
    }

    private async Task StartServerAsync()
    {
        var endpoint = _endpoint.Text.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            MessageBox.Show(this, "Enter the public endpoint (host or host:port) that clients should connect to.",
                "Endpoint required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!endpoint.Contains(':'))
            endpoint += ":" + _server.Config.Server.ListenPort;
        _endpoint.Text = endpoint;
        _server.UpdatePublicEndpoint(endpoint);

        await RunActionAsync(() => _server.Start(), "start the server");
        await RefreshEverythingAsync();
    }

    private async Task StopServerAsync()
    {
        await RunActionAsync(() => _server.Stop(), "stop the server");
        await RefreshEverythingAsync();
    }

    private async Task RunActionAsync(Action action, string description)
    {
        SetBusy(true);
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, $"Failed to {description}", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUiState();
            RefreshGridRows();
        }
    }

    private void AddPeer()
    {
        using var dialog = new AddPeerDialog($"device-{_server.Peers.Count + 1}");
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var peer = _server.AddPeer(dialog.PeerName, dialog.GenerateOnServer ? null : dialog.PublicKey);
            _server.PopulatePrivateKey(peer);
            RefreshGridRows();
            SelectPeer(peer);
            using var details = new PeerDetailsDialog(_server.Config.Server, peer);
            details.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not add peer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleSelectedPeer()
    {
        var peer = SelectedPeer;
        if (peer is null) return;
        try
        {
            _server.SetPeerEnabled(peer, !peer.Enabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not change the peer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        RefreshGridRows();
        SelectPeer(peer);
    }

    private void DeleteSelectedPeer()
    {
        var peer = SelectedPeer;
        if (peer is null) return;

        var confirm = MessageBox.Show(this, $"Delete peer '{peer.Name}'?", "Confirm delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try
        {
            _server.RemovePeer(peer);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not delete the peer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshGridRows();
    }

    private void ShowSelectedPeer()
    {
        var peer = SelectedPeer;
        if (peer is null) return;
        _server.PopulatePrivateKey(peer);
        using var dialog = new PeerDetailsDialog(_server.Config.Server, peer);
        dialog.ShowDialog(this);
    }

    private async Task DetectIpAsync()
    {
        SetBusy(true);
        var ip = await Task.Run(NetworkConfigurator.TryGetPublicIp);
        SetBusy(false);

        if (ip is null)
        {
            MessageBox.Show(this, "Could not detect the public IP address.", "Detection failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _endpoint.Text = $"{ip}:{_server.Config.Server.ListenPort}";
        AppendLog($"Detected public endpoint {_endpoint.Text}");
    }

    private async Task RefreshEverythingAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await Task.Run(() => _server.RefreshStats());
            _publicKey.Text = _server.Config.Server.PublicKey;
            if (!string.IsNullOrWhiteSpace(_server.Config.Server.PublicEndpoint) && !_endpoint.Focused)
                _endpoint.Text = _server.Config.Server.PublicEndpoint;
            RefreshGridRows();
            UpdateUiState();
        }
        catch (Exception ex)
        {
            AppendLog("refresh error: " + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshGridRows()
    {
        var selected = SelectedPeer?.PublicKey;
        _grid.Rows.Clear();

        foreach (var peer in _server.Peers)
        {
            var index = _grid.Rows.Add(
                peer.Name,
                peer.TunnelAddress,
                peer.Enabled ? "Enabled" : "Disabled",
                _server.IsRunning ? (peer.Online ? "Online" : "Offline") : "-",
                FormatBytes(peer.TxBytes),
                FormatBytes(peer.RxBytes),
                FormatHandshake(peer.LastHandshake),
                peer.PublicKey);

            _grid.Rows[index].Tag = peer;
            if (!peer.Enabled)
                _grid.Rows[index].DefaultCellStyle.ForeColor = Color.Gray;
        }

        if (selected is not null)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is Peer p && p.PublicKey == selected)
                {
                    row.Selected = true;
                    break;
                }
            }
        }
    }

    private void SelectPeer(Peer peer)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is Peer p && p.PublicKey == peer.PublicKey)
            {
                row.Selected = true;
                _grid.CurrentCell = row.Cells[0];
                break;
            }
        }
    }

    private Peer? SelectedPeer => _grid.CurrentRow?.Tag as Peer;

    private void UpdateUiState()
    {
        var running = _server.IsRunning;
        if (running)
        {
            _status.Text = $"Running - {_server.BackendName} - UDP {_server.Config.Server.ListenPort} - {_server.Config.Server.Subnet}";
            _status.ForeColor = Color.ForestGreen;
        }
        else
        {
            _status.Text = "Stopped";
            _status.ForeColor = Color.Firebrick;
        }

        _start.Enabled = !running;
        _stop.Enabled = running;
        _endpoint.Enabled = !running;
        _detect.Enabled = !running;

        var hasSelection = SelectedPeer is not null;
        _togglePeer.Enabled = hasSelection;
        _deletePeer.Enabled = hasSelection;
        _showPeer.Enabled = hasSelection;
        _addPeer.Enabled = true;
    }

    private void SetBusy(bool busy)
    {
        if (busy) _timer.Stop();
        else _timer.Start();

        UseWaitCursor = busy;
        _start.Enabled = !busy && !_server.IsRunning;
        _stop.Enabled = !busy && _server.IsRunning;
        _addPeer.Enabled = !busy;
        _togglePeer.Enabled = !busy && SelectedPeer is not null;
        _deletePeer.Enabled = !busy && SelectedPeer is not null;
        _showPeer.Enabled = !busy && SelectedPeer is not null;
        _detect.Enabled = !busy && !_server.IsRunning;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLog), message); } catch { /* form closing */ }
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatHandshake(DateTimeOffset? handshake)
    {
        if (handshake is null) return "never";
        var delta = DateTimeOffset.UtcNow - handshake.Value;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _server.Dispose();
        base.OnFormClosing(e);
    }
}
