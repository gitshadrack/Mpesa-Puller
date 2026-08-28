using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

sealed class MainForm : Form
{
    private readonly DatabaseWriter database;
    private readonly PullerOptions options;
    private readonly NotifyIcon trayIcon;
    private readonly Label statusLabel;
    private readonly Button openButton;
    private readonly Button closeButton;
    private readonly ToolStripMenuItem trayStartItem;
    private readonly ToolStripMenuItem trayStopItem;
    private readonly Label modemLabel;
    private readonly Label databaseLabel;
    private readonly Button testButton;
    private readonly TabControl tabs;
    private readonly TabPage settingsTab;
    private readonly Panel settingsPanel;
    private CancellationTokenSource? pollingCancellation;
    private Task? pollingTask;
    private bool closing;

    public MainForm(IConfiguration configuration, DatabaseWriter database)
    {
        this.database = database;
        options = configuration.GetSection("MpesaPuller").Get<PullerOptions>() ?? new PullerOptions();
        Text = "M-Pesa Message Puller";
        Icon = CreateMpesaIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(420, 220);
        Size = new Size(520, 280);

        var title = new Label { Text = "M-Pesa Message Puller", Dock = DockStyle.Top, Font = new Font("Segoe UI", 16, FontStyle.Bold), Height = 48, Padding = new Padding(18, 12, 0, 0) };
        statusLabel = new Label { Text = "Stopped", Dock = DockStyle.Top, Height = 34, Padding = new Padding(20, 5, 0, 0), ForeColor = Color.DimGray };
        modemLabel = new Label { Text = "Modem: not tested", Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 2, 0, 0) };
        databaseLabel = new Label { Text = "Database: not tested", Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 2, 0, 0) };
        openButton = new Button { Text = "Open / Start", Width = 120, Height = 36, Enabled = true };
        closeButton = new Button { Text = "Close / Stop", Width = 120, Height = 36, Enabled = false };
        var dummyButton = new Button { Text = "Insert Dummy Messages", Width = 160, Height = 36 };
        var previewButton = new Button { Text = "Preview Parser", Width = 110, Height = 36 };
        testButton = new Button { Text = "Test Modem", Width = 100, Height = 36 };
        var minimizeButton = new Button { Text = "Run in Background", Width = 150, Height = 36 };
        openButton.Click += async (_, _) => await StartPollingAsync();
        closeButton.Click += (_, _) => StopPolling();
        dummyButton.Click += async (_, _) => await InsertDummyMessagesAsync();
        previewButton.Click += (_, _) => PreviewParser();
        testButton.Click += async (_, _) => await TestModemAsync();
        minimizeButton.Click += (_, _) => MinimizeToTray();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8), AutoSize = false };
        buttons.Controls.AddRange(new Control[] { openButton, closeButton, dummyButton, previewButton, testButton, minimizeButton });
        var operationsPage = new TabPage("Operations");
        operationsPage.Controls.Add(buttons);
        operationsPage.Controls.Add(databaseLabel);
        operationsPage.Controls.Add(modemLabel);
        operationsPage.Controls.Add(statusLabel);
        operationsPage.Controls.Add(title);
        settingsPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        settingsTab = new TabPage("Settings");
        settingsTab.Controls.Add(settingsPanel);
        tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(operationsPage);
        tabs.TabPages.Add(settingsTab);
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == settingsTab) UnlockSettings(); };
        Controls.Add(tabs);

        trayIcon = new NotifyIcon { Icon = Icon, Text = "M-Pesa Message Puller", Visible = false };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayIcon.ContextMenuStrip = new ContextMenuStrip();
        trayIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayStartItem = new ToolStripMenuItem("Start") { Enabled = true };
        trayStopItem = new ToolStripMenuItem("Stop") { Enabled = false };
        trayStartItem.Click += async (_, _) => await StartPollingAsync();
        trayStopItem.Click += (_, _) => StopPolling();
        trayIcon.ContextMenuStrip.Items.Add(trayStartItem);
        trayIcon.ContextMenuStrip.Items.Add(trayStopItem);
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApplication());
        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) MinimizeToTray(); };
        BuildSettingsPanel(configuration);
    }

    private async Task StartPollingAsync()
    {
        if (pollingTask is { IsCompleted: false }) return;
        pollingCancellation = new CancellationTokenSource();
        openButton.Enabled = false;
        closeButton.Enabled = true;
        trayStartItem.Enabled = false;
        trayStopItem.Enabled = true;
        statusLabel.Text = "Running in background";
        pollingTask = PollAsync(pollingCancellation.Token);
        await Task.Yield();
    }

    private void UnlockSettings()
    {
        if (settingsPanel.Visible) return;
        using var passwordDialog = new Form { Text = "Settings Authorization", ClientSize = new Size(320, 135), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var prompt = new Label { Text = "Enter administrator password:", AutoSize = true, Location = new Point(18, 18) };
        var passwordBox = new TextBox { UseSystemPasswordChar = true, Width = 200, Location = new Point(18, 45) };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(145, 85), Width = 75 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(225, 85), Width = 75 };
        passwordDialog.Controls.AddRange(new Control[] { prompt, passwordBox, okButton, cancelButton });
        passwordDialog.AcceptButton = okButton;
        passwordDialog.CancelButton = cancelButton;
        if (passwordDialog.ShowDialog(this) != DialogResult.OK || passwordBox.Text != "123456")
        {
            tabs.SelectedIndex = 0;
            return;
        }
        settingsPanel.Visible = true;
    }

    private void BuildSettingsPanel(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Restaurant") ?? string.Empty;
        var connection = new SqlConnectionStringBuilder(connectionString);
        var serverBox = new TextBox { Text = connection.DataSource, Width = 300 };
        var databaseBox = new TextBox { Text = connection.InitialCatalog, Width = 300 };
        var userBox = new TextBox { Text = connection.UserID, Width = 300 };
        var passwordBox = new TextBox { Text = connection.Password, UseSystemPasswordChar = true, Width = 300 };
        var saveButton = new Button { Text = "Save Database Settings", Width = 180, Height = 36 };
        var note = new Label { Text = "Changes take effect after restarting the application.", AutoSize = true, ForeColor = Color.DimGray };
        saveButton.Click += (_, _) =>
        {
            try
            {
                var updated = new SqlConnectionStringBuilder
                {
                    DataSource = serverBox.Text.Trim(),
                    InitialCatalog = databaseBox.Text.Trim(),
                    UserID = userBox.Text.Trim(),
                    Password = passwordBox.Text,
                    TrustServerCertificate = true
                };
                Directory.CreateDirectory(ConfigPaths.UserSettingsDirectory);
                var root = File.Exists(ConfigPaths.UserSettingsFile)
                    ? JsonNode.Parse(File.ReadAllText(ConfigPaths.UserSettingsFile))?.AsObject() ?? new JsonObject()
                    : new JsonObject();
                var connectionStrings = root["ConnectionStrings"]?.AsObject() ?? new JsonObject();
                connectionStrings["Restaurant"] = updated.ConnectionString;
                root["ConnectionStrings"] = connectionStrings;
                File.WriteAllText(ConfigPaths.UserSettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show(this, $"Database settings saved for this Windows user. Restart the app to apply them.\n\n{ConfigPaths.UserSettingsFile}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Could not save settings: {exception.Message}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20), ColumnCount = 2, RowCount = 6 };
        form.Controls.Add(new Label { Text = "SQL Server / Instance", AutoSize = true }, 0, 0); form.Controls.Add(serverBox, 1, 0);
        form.Controls.Add(new Label { Text = "Database", AutoSize = true }, 0, 1); form.Controls.Add(databaseBox, 1, 1);
        form.Controls.Add(new Label { Text = "User", AutoSize = true }, 0, 2); form.Controls.Add(userBox, 1, 2);
        form.Controls.Add(new Label { Text = "Password", AutoSize = true }, 0, 3); form.Controls.Add(passwordBox, 1, 3);
        form.Controls.Add(saveButton, 1, 4); form.Controls.Add(note, 1, 5);
        settingsPanel.Controls.Add(form);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settings = await database.GetSettingsAsync(cancellationToken);
                BeginInvoke(() => databaseLabel.Text = "Database: connected");
                if (settings.CheckMessages)
                {
                    var configuredPort = settings.PortName ?? options.PortName;
                    var portName = GsmModem.FindPort(options with { PortName = configuredPort, SimPin = settings.Pin });
                    if (portName is null)
                    {
                        BeginInvoke(() => statusLabel.Text = string.IsNullOrWhiteSpace(configuredPort) ? "Waiting: insert GSM modem" : $"Waiting for modem on {configuredPort}");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollingSeconds)), cancellationToken);
                        continue;
                    }
                    if (!string.Equals(settings.PortName, portName, StringComparison.OrdinalIgnoreCase)) await database.SavePortNameAsync(portName, cancellationToken);
                    var modemOptions = options with { PortName = portName, SimPin = settings.Pin };
                    BeginInvoke(() => statusLabel.Text = $"Connected to modem on {portName}");
                    using var modem = new GsmModem(modemOptions);
                    modem.Open();
                    var diagnostics = modem.ReadDiagnostics();
                    BeginInvoke(() => modemLabel.Text = $"Modem: {portName} | SIM {diagnostics.SimCard} | Signal {diagnostics.Signal} | {diagnostics.Network}");
                    foreach (var sms in modem.ReadUnreadMessages())
                    {
                        if (!SmsParser.IsMpesa(sms))
                        {
                            if (modemOptions.DiscardNonMpesaMessages) modem.DeleteMessage(sms.Index);
                            continue;
                        }
                        var imported = await database.ImportAsync(sms, settings, cancellationToken);
                        if (imported)
                        {
                            if (settings.PrintReceipt)
                            {
                                try
                                {
                                    ReceiptPrinter.Print(sms, settings);
                                }
                                catch (Exception printException)
                                {
                                    BeginInvoke(() => statusLabel.Text = $"Payment saved; receipt failed: {printException.Message}");
                                }
                            }
                            if (modemOptions.DeleteAfterImport) modem.DeleteMessage(sms.Index);
                        }
                        else if (modemOptions.DeleteAfterImport)
                        {
                            modem.DeleteMessage(sms.Index);
                        }
                    }
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                BeginInvoke(() => databaseLabel.Text = exception is SqlException ? "Database: connection error" : databaseLabel.Text);
                BeginInvoke(() => statusLabel.Text = $"Error: {exception.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollingSeconds)), cancellationToken);
        }
    }

    private void StopPolling()
    {
        pollingCancellation?.Cancel();
        pollingCancellation?.Dispose();
        pollingCancellation = null;
        pollingTask = null;
        openButton.Enabled = true;
        closeButton.Enabled = false;
        trayStartItem.Enabled = true;
        trayStopItem.Enabled = false;
        statusLabel.Text = "Stopped";
    }

    private async Task InsertDummyMessagesAsync()
    {
        using var passwordDialog = new Form { Text = "Authorization Required", ClientSize = new Size(320, 135), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var prompt = new Label { Text = "Enter administrator password:", AutoSize = true, Location = new Point(18, 18) };
        var passwordBox = new TextBox { UseSystemPasswordChar = true, Width = 200, Location = new Point(18, 45) };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(145, 85), Width = 75 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(225, 85), Width = 75 };
        passwordDialog.Controls.AddRange(new Control[] { prompt, passwordBox, okButton, cancelButton });
        passwordDialog.AcceptButton = okButton;
        passwordDialog.CancelButton = cancelButton;
        if (passwordDialog.ShowDialog(this) != DialogResult.OK || passwordBox.Text != "123456") { statusLabel.Text = "Dummy insert not authorized"; return; }
        var inserted = await database.InsertDummyMessagesAsync(CancellationToken.None);
        statusLabel.Text = $"Inserted {inserted} dummy M-Pesa messages";
    }

    private void PreviewParser()
    {
        const string sample = "QK12AB34CD Confirmed. Ksh1,250.00 received from Jane Doe 254712345678 on 28/08/26 at 18:30. Transaction cost Ksh12.50. Bill 445566.";
        var parsed = SmsParser.Parse(0, "MPESA", "26/08/28", "18:30:00", sample);
        MessageBox.Show($"Transaction: {parsed.TransactionNo}\nAmount: Ksh {parsed.Amount:N2}\nCharges: Ksh {parsed.Charges:N2}\nMobile: {parsed.MobileNo}\nSender: {parsed.SenderName}\nBill/Reference: {parsed.BillNo}\n\nNo database changes were made.", "Parser Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task TestModemAsync()
    {
        testButton.Enabled = false;
        modemLabel.Text = "Modem: testing...";
        try
        {
            var result = await Task.Run(async () =>
            {
                var settings = await database.GetSettingsAsync(CancellationToken.None);
                var portName = GsmModem.FindPort(options with { PortName = settings.PortName ?? options.PortName, SimPin = settings.Pin });
                if (portName is null) return (PortName: (string?)null, Diagnostics: (ModemDiagnostics?)null);
                using var modem = new GsmModem(options with { PortName = portName, SimPin = settings.Pin });
                modem.Open();
                var diagnostics = modem.ReadDiagnostics();
                if (!string.Equals(settings.PortName, portName, StringComparison.OrdinalIgnoreCase)) await database.SavePortNameAsync(portName, CancellationToken.None);
                return (PortName: (string?)portName, Diagnostics: (ModemDiagnostics?)diagnostics);
            });
            modemLabel.Text = result.PortName is null ? "Modem: not found" : $"Modem: {result.PortName} | SIM {result.Diagnostics!.SimCard} | Signal {result.Diagnostics.Signal} | {result.Diagnostics.Network}";
        }
        catch (Exception exception) { modemLabel.Text = $"Modem test failed: {exception.Message}"; }
        finally { testButton.Enabled = true; }
    }

    private void MinimizeToTray() { trayIcon.Visible = true; Hide(); trayIcon.ShowBalloonTip(1500, "M-Pesa Puller", "Still running in the background", ToolTipIcon.Info); }
    private void RestoreFromTray() { trayIcon.Visible = false; Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OnFormClosing(object? sender, FormClosingEventArgs e) { if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; MinimizeToTray(); return; } closing = true; StopPolling(); trayIcon.Visible = false; trayIcon.Dispose(); }
    private void ExitApplication() { closing = true; Close(); }

    private static Icon CreateMpesaIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(0, 116, 74));
        using var font = new Font("Segoe UI", 36, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        graphics.DrawString("M", font, brush, 8, 8);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
