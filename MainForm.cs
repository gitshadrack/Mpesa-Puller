using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Win32;
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
    private readonly Label paymentsLabel;
    private readonly Label lastPaymentLabel;
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
        MaximizeBox = false;
        MinimumSize = new Size(600, 500);
        Size = new Size(600, 500);

        var title = new Label { Text = "M-Pesa Message Puller", Dock = DockStyle.Top, Font = new Font("Segoe UI", 16, FontStyle.Bold), Height = 48, Padding = new Padding(18, 12, 0, 0) };
        statusLabel = new Label { Text = "● Stopped", Dock = DockStyle.Top, Height = 34, Padding = new Padding(20, 5, 0, 0), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        modemLabel = new Label { Text = "Modem: not tested", Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 2, 0, 0) };
        databaseLabel = new Label { Text = "Database: not tested", Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 2, 0, 0) };
        paymentsLabel = new Label { Text = "Today: loading payments...", Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 2, 0, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        lastPaymentLabel = new Label { Text = "Last payment: none", Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 2, 0, 0) };
        openButton = new Button { Text = "Start Puller", Width = 120, Height = 36, Enabled = true, BackColor = Color.FromArgb(31, 128, 74), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        closeButton = new Button { Text = "Stop Puller", Width = 120, Height = 36, Enabled = false, BackColor = Color.FromArgb(190, 47, 47), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var previewButton = new Button { Text = "Preview Parser", Width = 110, Height = 36 };
        testButton = new Button { Text = "Test Modem", Width = 100, Height = 36 };
        openButton.Click += async (_, _) => await StartPollingAsync();
        closeButton.Click += (_, _) => StopPolling();
        previewButton.Click += (_, _) => PreviewParser();
        testButton.Click += async (_, _) => await TestModemAsync();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8), AutoSize = false };
        buttons.Controls.AddRange(new Control[] { openButton, closeButton, testButton, previewButton });
        var operationsPage = new TabPage("Operations");
        operationsPage.Controls.Add(buttons);
        operationsPage.Controls.Add(databaseLabel);
        operationsPage.Controls.Add(modemLabel);
        operationsPage.Controls.Add(paymentsLabel);
        operationsPage.Controls.Add(lastPaymentLabel);
        operationsPage.Controls.Add(statusLabel);
        operationsPage.Controls.Add(title);
        settingsPanel = new Panel { Dock = DockStyle.Fill, Visible = false, AutoScroll = true };
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
        SetStatus("Starting puller...", Color.DarkGoldenrod);
        // Modem commands use synchronous serial I/O and must not run on the WinForms UI thread.
        var cancellationToken = pollingCancellation.Token;
        pollingTask = Task.Run(() => PollAsync(cancellationToken));
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
        var findInstancesButton = new Button { Text = "Find SQL Instances", AutoSize = true, Height = 28 };
        var serverControls = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        serverControls.Controls.Add(serverBox);
        serverControls.Controls.Add(findInstancesButton);
        var databaseBox = new TextBox { Text = connection.InitialCatalog, Width = 300 };
        var userBox = new TextBox { Text = connection.UserID, Width = 300 };
        var passwordBox = new TextBox { Text = connection.Password, UseSystemPasswordChar = true, Width = 300 };
        var profileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        profileBox.Items.AddRange(MessageProfiles.Formats);
        profileBox.SelectedItem = MessageProfiles.FormatFor(options.MessageProfile);
        var entityBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        void UpdateEntityOptions()
        {
            var selectedFormat = profileBox.SelectedItem?.ToString() ?? MessageProfiles.AutoDetect;
            var selectedProfile = MessageProfiles.Normalize(options.MessageProfile);
            var selectedEntity = MessageProfiles.EntityOptions(selectedFormat)
                .FirstOrDefault(item => string.Equals(item, selectedProfile, StringComparison.OrdinalIgnoreCase));
            entityBox.Items.Clear();
            entityBox.Items.AddRange(MessageProfiles.EntityOptions(selectedFormat).Cast<object>().ToArray());
            entityBox.SelectedItem = selectedEntity ?? entityBox.Items[0];
        }
        profileBox.SelectedIndexChanged += (_, _) => UpdateEntityOptions();
        UpdateEntityOptions();
        var saveButton = new Button { Text = "Save Database Settings", Width = 180, Height = 36 };
        var testDatabaseButton = new Button { Text = "Test Active DB Connection", Width = 180, Height = 36 };
        var dummyButton = new Button { Text = "Insert Dummy Messages", Width = 180, Height = 36 };
        var note = new Label { Text = "Changes take effect after restarting the application.", AutoSize = true, ForeColor = Color.DimGray };
        dummyButton.Click += async (_, _) => await InsertDummyMessagesAsync();
        testDatabaseButton.Click += async (_, _) => await TestDatabaseConnectionAsync();
        findInstancesButton.Click += (_, _) => SelectSqlInstance(serverBox);
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
                var pullerSettings = root["MpesaPuller"]?.AsObject() ?? new JsonObject();
                pullerSettings["MessageProfile"] = MessageProfiles.ProfileFor(profileBox.SelectedItem?.ToString(), entityBox.SelectedItem?.ToString());
                root["MpesaPuller"] = pullerSettings;
                File.WriteAllText(ConfigPaths.UserSettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show(this, $"Database settings saved for this Windows user. Restart the app to apply them.\n\n{ConfigPaths.UserSettingsFile}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                AppLog.Error("Saving settings failed.", exception);
                MessageBox.Show(this, $"Could not save settings: {exception.Message}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(20), ColumnCount = 2, RowCount = 10 };
        form.Controls.Add(new Label { Text = "SQL Server / Instance", AutoSize = true }, 0, 0); form.Controls.Add(serverControls, 1, 0);
        form.Controls.Add(new Label { Text = "Database", AutoSize = true }, 0, 1); form.Controls.Add(databaseBox, 1, 1);
        form.Controls.Add(new Label { Text = "User", AutoSize = true }, 0, 2); form.Controls.Add(userBox, 1, 2);
        form.Controls.Add(new Label { Text = "Password", AutoSize = true }, 0, 3); form.Controls.Add(passwordBox, 1, 3);
        form.Controls.Add(new Label { Text = "Parser Template", AutoSize = true }, 0, 4); form.Controls.Add(profileBox, 1, 4);
        form.Controls.Add(new Label { Text = "Entity / Template Type", AutoSize = true }, 0, 5); form.Controls.Add(entityBox, 1, 5);
        form.Controls.Add(testDatabaseButton, 1, 6); form.Controls.Add(saveButton, 1, 7); form.Controls.Add(dummyButton, 1, 8); form.Controls.Add(note, 1, 9);
        settingsPanel.Controls.Add(form);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var dashboardLoaded = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? activePort = null;
            try
            {
                var settings = await database.GetSettingsAsync(cancellationToken);
                BeginInvoke(() => databaseLabel.Text = "Database: connected");
                if (!dashboardLoaded)
                {
                    await RefreshDashboardAsync(cancellationToken);
                    dashboardLoaded = true;
                }
                if (settings.CheckMessages)
                {
                    var configuredPort = settings.PortName ?? options.PortName;
                    var portName = GsmModem.FindPort(options with { PortName = configuredPort, SimPin = settings.Pin });
                    if (portName is null)
                    {
                        BeginInvoke(() => SetStatus(string.IsNullOrWhiteSpace(configuredPort) ? "Waiting: insert GSM modem" : $"Waiting for modem on {configuredPort}", Color.DarkGoldenrod));
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollingSeconds)), cancellationToken);
                        continue;
                    }
                    if (!string.Equals(settings.PortName, portName, StringComparison.OrdinalIgnoreCase)) await database.SavePortNameAsync(portName, cancellationToken);
                    var modemOptions = options with { PortName = portName, SimPin = settings.Pin };
                    activePort = portName;
                    BeginInvoke(() => SetStatus($"Connected — pulling payments from {portName}", Color.FromArgb(31, 128, 74)));
                    using var modem = new GsmModem(modemOptions);
                    BeginInvoke(() => modemLabel.Text = $"Modem: {portName} | checking SIM...");
                    modem.Open();
                    var diagnostics = modem.ReadDiagnostics();
                    BeginInvoke(() => modemLabel.Text = $"Modem: {portName} | SIM {diagnostics.SimCard} | Signal {diagnostics.Signal} | {diagnostics.Network}");
                    foreach (var sms in modem.ReadUnreadMessages())
                    {
                        if (!SmsParser.IsIncomingPayment(sms, modemOptions.MessageProfile))
                        {
                            if (modemOptions.DiscardNonMpesaMessages) modem.DeleteMessage(sms.Index);
                            continue;
                        }
                        var imported = await database.ImportAsync(sms, settings, cancellationToken);
                        if (imported)
                        {
                            await RefreshDashboardAsync(cancellationToken);
                            BeginInvoke(() => SetStatus("Payment captured successfully", Color.FromArgb(31, 128, 74)));
                            if (settings.PrintReceipt)
                            {
                                try
                                {
                                    ReceiptPrinter.Print(sms, settings);
                                }
                                catch (Exception printException)
                                {
                                    AppLog.Error("Receipt printing failed after the payment was saved.", printException);
                                    BeginInvoke(() => SetStatus($"Payment saved; receipt failed: {printException.Message}", Color.DarkGoldenrod));
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
                else
                {
                    BeginInvoke(() => SetStatus("Stopped by database setting", Color.DimGray));
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Error("Polling error.", exception);
                BeginInvoke(() => databaseLabel.Text = exception is SqlException ? "Database: connection error" : databaseLabel.Text);
                if (!string.IsNullOrWhiteSpace(activePort) && exception.Message.Contains("SIM", StringComparison.OrdinalIgnoreCase))
                {
                    BeginInvoke(() => modemLabel.Text = $"Modem: {activePort} | {DescribeSimStatus(exception.Message)}");
                }
                BeginInvoke(() => SetStatus($"Error: {exception.Message}", Color.Firebrick));
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
        SetStatus("Stopped", Color.DimGray);
    }

    private void SetStatus(string message, Color color)
    {
        statusLabel.Text = $"● {message}";
        statusLabel.ForeColor = color;
    }

    private static string DescribeSimStatus(string errorMessage)
    {
        if (errorMessage.Contains("PUK", StringComparison.OrdinalIgnoreCase) || errorMessage.Contains("BLOCK", StringComparison.OrdinalIgnoreCase)) return "SIM blocked — PUK required";
        if (errorMessage.Contains("PIN", StringComparison.OrdinalIgnoreCase)) return "SIM PIN error";
        if (errorMessage.Contains("not detected", StringComparison.OrdinalIgnoreCase)) return "SIM card not detected";
        return "SIM unavailable";
    }

    private async Task RefreshDashboardAsync(CancellationToken cancellationToken)
    {
        var summary = await database.GetDashboardSummaryAsync(cancellationToken);
        BeginInvoke(() =>
        {
            paymentsLabel.Text = $"Today: {summary.PaymentCount:N0} payment(s) | Ksh {summary.TotalAmount:N2}";
            lastPaymentLabel.Text = summary.LastTransactionNo is null
                ? "Last payment: none"
                : $"Last payment: Ksh {summary.LastAmount ?? 0:N2} from {summary.LastSenderName ?? "Unknown"} ({summary.LastTransactionNo}) at {summary.LastTime ?? "-"}";
        });
    }

    private async Task TestDatabaseConnectionAsync()
    {
        try
        {
            await database.TestConnectionAsync(CancellationToken.None);
            databaseLabel.Text = "Database: connected";
            SetStatus("Database connection successful", Color.FromArgb(31, 128, 74));
        }
        catch (Exception exception)
        {
            AppLog.Error("Database connection test failed.", exception);
            databaseLabel.Text = "Database: connection error";
            SetStatus($"Database error: {exception.Message}", Color.Firebrick);
        }
    }

    private void SelectSqlInstance(TextBox serverBox)
    {
        var instances = FindLocalSqlInstances();
        if (instances.Count == 0)
        {
            MessageBox.Show(this, "No local SQL Server instances were found. Ensure SQL Server is installed, then enter SERVER\\INSTANCE manually.", "Find SQL Instances", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form { Text = "Local SQL Server Instances", ClientSize = new Size(420, 150), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var prompt = new Label { Text = "Select the instance to use:", AutoSize = true, Location = new Point(18, 18) };
        var instancesBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(18, 45), Width = 380 };
        instancesBox.Items.AddRange(instances.Cast<object>().ToArray());
        instancesBox.SelectedItem = instances.FirstOrDefault(instance => string.Equals(instance, serverBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)) ?? instances[0];
        var selectButton = new Button { Text = "Use Selected", DialogResult = DialogResult.OK, Location = new Point(222, 95), Width = 90 };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 95), Width = 80 };
        dialog.Controls.AddRange(new Control[] { prompt, instancesBox, selectButton, cancelButton });
        dialog.AcceptButton = selectButton;
        dialog.CancelButton = cancelButton;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        serverBox.Text = instancesBox.SelectedItem!.ToString();
        SetStatus("SQL instance selected — save and restart to apply it", Color.DarkGoldenrod);
    }

    private static List<string> FindLocalSqlInstances()
    {
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var sqlInstances = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                if (sqlInstances is null) continue;
                foreach (var instanceName in sqlInstances.GetValueNames())
                {
                    instances.Add(string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "." : $".\\{instanceName}");
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Some locked-down Windows installations do not expose the SQL registry keys.
            }
        }
        return instances.OrderBy(instance => instance, StringComparer.OrdinalIgnoreCase).ToList();
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
        try
        {
            var inserted = await database.InsertDummyMessagesAsync(CancellationToken.None);
            SetStatus($"Inserted {inserted} dummy M-Pesa messages", Color.FromArgb(31, 128, 74));
            await RefreshDashboardAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppLog.Error("Inserting dummy messages failed.", exception);
            SetStatus($"Database error: {exception.Message}", Color.Firebrick);
        }
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
        catch (Exception exception)
        {
            AppLog.Error("Modem test failed.", exception);
            modemLabel.Text = exception.Message.Contains("SIM", StringComparison.OrdinalIgnoreCase)
                ? $"Modem: {DescribeSimStatus(exception.Message)}"
                : $"Modem test failed: {exception.Message}";
        }
        finally { testButton.Enabled = true; }
    }

    private void MinimizeToTray() { trayIcon.Visible = true; Hide(); trayIcon.ShowBalloonTip(1500, "M-Pesa Puller", "Still running in the background", ToolTipIcon.Info); }
    private void RestoreFromTray() { trayIcon.Visible = false; Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OnFormClosing(object? sender, FormClosingEventArgs e) { if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; MinimizeToTray(); return; } closing = true; StopPolling(); trayIcon.Visible = false; trayIcon.Dispose(); }
    private void ExitApplication() { closing = true; Close(); }

    private static Icon CreateMpesaIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "mpesa.ico");
        if (File.Exists(iconPath))
        {
            using var icon = new Icon(iconPath);
            return (Icon)icon.Clone();
        }

        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(0, 116, 74));
        using var font = new Font("Segoe UI", 36, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        graphics.DrawString("M", font, brush, 8, 8);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
