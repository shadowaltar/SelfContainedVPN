using MyVpn.Server.Core;

namespace MyVpn.Server.UI;

/// <summary>
/// Collects the details needed to add a peer. The recommended flow takes the client's public
/// key so the server never knows the private key; generating on the server is an explicit opt-in.
/// </summary>
public sealed class AddPeerDialog : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _publicKey = new();
    private readonly CheckBox _generateOnServer = new();

    public AddPeerDialog(string suggestedName)
    {
        Text = "Add peer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 320);
        Font = new Font("Segoe UI", 9f);

        const int contentWidth = 520;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 12),
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var nameLabel = new Label
        {
            Text = "Device name:",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 3),
        };

        _name.Text = suggestedName;
        _name.Dock = DockStyle.Fill;
        _name.Margin = new Padding(0, 0, 0, 12);

        var keyLabel = new Label
        {
            Text = "Client public key - in the MyVpn app tap \"Generate key\", then paste the public key here:",
            AutoSize = true,
            MaximumSize = new Size(contentWidth, 0),
            Margin = new Padding(0, 0, 0, 3),
        };

        _publicKey.Dock = DockStyle.Fill;
        _publicKey.Font = new Font("Consolas", 9f);
        _publicKey.Margin = new Padding(0, 0, 0, 12);

        _generateOnServer.Text = "Generate the key on the server instead (the server will then know the private key)";
        _generateOnServer.AutoSize = true;
        _generateOnServer.MaximumSize = new Size(contentWidth, 0);
        _generateOnServer.Margin = new Padding(0, 0, 0, 8);
        _generateOnServer.CheckedChanged += (_, _) => _publicKey.Enabled = !_generateOnServer.Checked;

        var hint = new Label
        {
            Text = "The server never needs the client's private key. Pasting the client's public key is the safest option.",
            AutoSize = true,
            MaximumSize = new Size(contentWidth, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 12),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0),
        };

        var ok = new Button
        {
            Text = "Add",
            DialogResult = DialogResult.OK,
            Width = 96,
            Height = 34,
            Margin = new Padding(8, 0, 0, 0),
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 96,
            Height = 34,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        ok.Click += (_, _) =>
        {
            if (!GenerateOnServer && !WgKeys.IsValidKey(_publicKey.Text))
            {
                MessageBox.Show(
                    this,
                    "Enter a valid client public key, or tick \"Generate the key on the server\".",
                    "Invalid public key",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        layout.Controls.Add(nameLabel);
        layout.Controls.Add(_name);
        layout.Controls.Add(keyLabel);
        layout.Controls.Add(_publicKey);
        layout.Controls.Add(_generateOnServer);
        layout.Controls.Add(hint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _name;
    }

    public string PeerName => string.IsNullOrWhiteSpace(_name.Text) ? "peer" : _name.Text.Trim();

    public string PublicKey => _publicKey.Text.Trim();

    public bool GenerateOnServer => _generateOnServer.Checked;
}
