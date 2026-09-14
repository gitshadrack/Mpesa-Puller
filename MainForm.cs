using System.Drawing;
using System.Diagnostics;
using System.IO.Ports;
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
    private readonly TabPage messagesTab;
    private readonly DataGridView messagesGrid;
    private readonly Label messagesStatusLabel;
    private readonly DateTimePicker messagesDatePicker;
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
        MinimumSize = new Size(760, 520);
        Size = new Size(900, 600);

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
        var openLogsButton = new Button { Text = "Open Logs Folder", Width = 130, Height = 36 };
        openButton.Click += async (_, _) => await StartPollingAsync();
        closeButton.Click += (_, _) => RequestStopPolling();
        previewButton.Click += (_, _) => PreviewParser();
        testButton.Click += async (_, _) => await TestModemAsync();
        openLogsButton.Click += (_, _) => OpenLogsFolder();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8), AutoSize = false };
        buttons.Controls.AddRange(new Control[] { openButton, closeButton, testButton, previewButton, openLogsButton });
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
        messagesGrid = CreateMessagesGrid();
        messagesStatusLabel = new Label { Text = "Open this tab to load recent messages.", AutoSize = true, Padding = new Padding(8, 10, 8, 0) };
        messagesDatePicker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "dd-MM-yyyy", Value = DateTime.Today, Width = 125 };
        var refreshMessagesButton = new Button { Text = "Refresh", Width = 100, Height = 34 };
        var printSelectedButton = new Button { Text = "Print Selected", Width = 130, Height = 34 };
        refreshMessagesButton.Click += async (_, _) => await RefreshMessagesAsync();
        messagesDatePicker.ValueChanged += async (_, _) => await RefreshMessagesAsync();
        printSelectedButton.Click += async (_, _) => await PrintSelectedMessagesAsync();
        var messageButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8), WrapContents = false };
        messageButtons.Controls.AddRange(new Control[]
        {
            new Label { Text = "Payment date:", AutoSize = true, Padding = new Padding(0, 9, 0, 0) },
            messagesDatePicker,
            refreshMessagesButton,
            printSelectedButton,
            messagesStatusLabel
        });
        messagesTab = new TabPage("Messages");
        messagesTab.Controls.Add(messagesGrid);
        messagesTab.Controls.Add(messageButtons);
        tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(operationsPage);
        tabs.TabPages.Add(messagesTab);
        tabs.TabPages.Add(settingsTab);
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (tabs.SelectedTab == settingsTab) UnlockSettings();
            else if (tabs.SelectedTab == messagesTab) await RefreshMessagesAsync();
        };
        Controls.Add(tabs);

        trayIcon = new NotifyIcon { Icon = Icon, Text = "M-Pesa Message Puller", Visible = false };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayIcon.ContextMenuStrip = new ContextMenuStrip();
        trayIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayStartItem = new ToolStripMenuItem("Start") { Enabled = true };
        trayStopItem = new ToolStripMenuItem("Stop") { Enabled = false };
        trayStartItem.Click += async (_, _) => await StartPollingAsync();
        trayStopItem.Click += (_, _) => RequestStopPolling();
        trayIcon.ContextMenuStrip.Items.Add(trayStartItem);
        trayIcon.ContextMenuStrip.Items.Add(trayStopItem);
        trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApplication());
        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) MinimizeToTray(); };
        BuildSettingsPanel(configuration);
    }

    private static DataGridView CreateMessagesGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "Date", FillWeight = 72 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time", FillWeight = 58 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Transaction", HeaderText = "Transaction", FillWeight = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sender", HeaderText = "Customer", FillWeight = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mobile", HeaderText = "Mobile", FillWeight = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Amount", HeaderText = "Amount", FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Format = "N2", Alignment = DataGridViewContentAlignment.MiddleRight } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reference", HeaderText = "Reference", FillWeight = 90 });
        return grid;
    }

    private async Task RefreshMessagesAsync()
    {
        try
        {
            messagesStatusLabel.Text = "Loading messages...";
            var selectedDate = messagesDatePicker.Value.Date;
            var messages = await database.GetMessagesByDateAsync(selectedDate, 2000, CancellationToken.None);
            messagesGrid.Rows.Clear();
            foreach (var message in messages)
            {
                var rowIndex = messagesGrid.Rows.Add(message.Date.ToString("dd-MM-yyyy"), message.Time, message.TransactionNo ?? "Review", message.SenderName, message.MobileNo, message.Amount, message.BillNo ?? "-");
                messagesGrid.Rows[rowIndex].Tag = message;
            }
            messagesGrid.ClearSelection();
            messagesStatusLabel.Text = $"{messages.Count:N0} message(s) on {selectedDate:dd-MM-yyyy}";
        }
        catch (Exception exception)
        {
            AppLog.Error("Loading message list failed.", exception);
            messagesStatusLabel.Text = $"Could not load messages: {exception.GetBaseException().Message}";
        }
    }

    private async Task PrintSelectedMessagesAsync()
    {
        var selectedMessages = messagesGrid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(row => row.Index)
            .Select(row => row.Tag)
            .OfType<SmsMessage>()
            .ToArray();
        if (selectedMessages.Length == 0)
        {
            MessageBox.Show(this, "Select one or more messages to print.", "Print Messages", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Print {selectedMessages.Length} selected receipt(s)?", "Print Messages", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            messagesStatusLabel.Text = "Printing selected messages...";
            var settings = await database.GetSettingsAsync(CancellationToken.None);
            await Task.Run(() =>
            {
                foreach (var message in selectedMessages) ReceiptPrinter.Print(message, settings);
            });
            messagesStatusLabel.Text = $"Printed {selectedMessages.Length} selected receipt(s).";
        }
        catch (Exception exception)
        {
            AppLog.Error("Manual receipt printing failed.", exception);
            messagesStatusLabel.Text = $"Print failed: {exception.GetBaseException().Message}";
            MessageBox.Show(this, exception.GetBaseException().Message, "Print Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        var saveButton = new Button { Text = "Save Settings", Width = 180, Height = 36 };
        var testDatabaseButton = new Button { Text = "Test Entered Connection", Width = 180, Height = 36 };
        var testModemButton = new Button { Text = "Test Modem", Width = 140, Height = 36 };
        var openLogsButton = new Button { Text = "Open Logs Folder", Width = 140, Height = 36 };
        var dummyButton = new Button { Text = "Insert Dummy Messages", Width = 180, Height = 36 };
        var note = new Label { Text = "Database changes apply when saved. Parser-template changes apply after restarting the application.", AutoSize = true, ForeColor = Color.DimGray };
        var remoteServerNote = new Label
        {
            Text = "Remote examples: 192.168.1.20,1433 or SERVERNAME\\INSTANCE. SQL Server must allow TCP/IP and its firewall port.",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.DimGray
        };
        string BuildEnteredConnectionString()
        {
            if (string.IsNullOrWhiteSpace(serverBox.Text)) throw new InvalidOperationException("Enter the remote SQL Server name, IP address, or IP address and port.");
            if (string.IsNullOrWhiteSpace(databaseBox.Text)) throw new InvalidOperationException("Enter the database name.");
            if (string.IsNullOrWhiteSpace(userBox.Text)) throw new InvalidOperationException("Enter the SQL Server login name.");

            return new SqlConnectionStringBuilder
            {
                DataSource = serverBox.Text.Trim(),
                InitialCatalog = databaseBox.Text.Trim(),
                UserID = userBox.Text.Trim(),
                Password = passwordBox.Text,
                IntegratedSecurity = false,
                TrustServerCertificate = true,
                ConnectTimeout = 10
            }.ConnectionString;
        }
        dummyButton.Click += async (_, _) => await InsertDummyMessagesAsync();
        testDatabaseButton.Click += async (_, _) =>
        {
            try
            {
                testDatabaseButton.Enabled = false;
                SetStatus("Testing entered database connection...", Color.DarkGoldenrod);
                await DatabaseWriter.TestConnectionAsync(BuildEnteredConnectionString(), CancellationToken.None);
                databaseLabel.Text = $"Database: connected to {serverBox.Text.Trim()}";
                SetStatus("Entered database connection successful", Color.FromArgb(31, 128, 74));
            }
            catch (Exception exception)
            {
                AppLog.Error("Entered database connection test failed.", exception);
                databaseLabel.Text = "Database: connection error";
                SetStatus($"Database error: {exception.GetBaseException().Message}", Color.Firebrick);
            }
            finally
            {
                testDatabaseButton.Enabled = true;
            }
        };
        testModemButton.Click += async (_, _) => await TestModemAsync();
        openLogsButton.Click += (_, _) => OpenLogsFolder();
        findInstancesButton.Click += (_, _) => SelectSqlInstance(serverBox);
        saveButton.Click += (_, _) =>
        {
            try
            {
                var updatedConnectionString = BuildEnteredConnectionString();
                Directory.CreateDirectory(ConfigPaths.UserSettingsDirectory);
                var root = File.Exists(ConfigPaths.UserSettingsFile)
                    ? JsonNode.Parse(File.ReadAllText(ConfigPaths.UserSettingsFile))?.AsObject() ?? new JsonObject()
                    : new JsonObject();
                var connectionStrings = root["ConnectionStrings"]?.AsObject() ?? new JsonObject();
                connectionStrings["Restaurant"] = updatedConnectionString;
                root["ConnectionStrings"] = connectionStrings;
                var pullerSettings = root["MpesaPuller"]?.AsObject() ?? new JsonObject();
                pullerSettings["MessageProfile"] = MessageProfiles.ProfileFor(profileBox.SelectedItem?.ToString(), entityBox.SelectedItem?.ToString());
                root["MpesaPuller"] = pullerSettings;
                File.WriteAllText(ConfigPaths.UserSettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                database.UpdateConnectionString(updatedConnectionString);
                databaseLabel.Text = $"Database: configured for {serverBox.Text.Trim()}";
                MessageBox.Show(this, $"Database settings saved and applied for this Windows user.\n\n{ConfigPaths.UserSettingsFile}\n\nRestart only if you changed the parser template.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                AppLog.Error("Saving settings failed.", exception);
                MessageBox.Show(this, $"Could not save settings: {exception.Message}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var databaseSection = new TableLayoutPanel { AutoSize = true, Padding = new Padding(12), ColumnCount = 2, RowCount = 7, Dock = DockStyle.Fill };
        databaseSection.Controls.Add(new Label { Text = "SQL Server / Instance", AutoSize = true }, 0, 0); databaseSection.Controls.Add(serverControls, 1, 0);
        databaseSection.Controls.Add(remoteServerNote, 1, 1);
        databaseSection.Controls.Add(new Label { Text = "Database", AutoSize = true }, 0, 2); databaseSection.Controls.Add(databaseBox, 1, 2);
        databaseSection.Controls.Add(new Label { Text = "User", AutoSize = true }, 0, 3); databaseSection.Controls.Add(userBox, 1, 3);
        databaseSection.Controls.Add(new Label { Text = "Password", AutoSize = true }, 0, 4); databaseSection.Controls.Add(passwordBox, 1, 4);
        databaseSection.Controls.Add(testDatabaseButton, 1, 5); databaseSection.Controls.Add(saveButton, 1, 6);

        var parserSection = new TableLayoutPanel { AutoSize = true, Padding = new Padding(12), ColumnCount = 2, RowCount = 3, Dock = DockStyle.Fill };
        parserSection.Controls.Add(new Label { Text = "Parser Template", AutoSize = true }, 0, 0); parserSection.Controls.Add(profileBox, 1, 0);
        parserSection.Controls.Add(new Label { Text = "Entity / Template Type", AutoSize = true }, 0, 1); parserSection.Controls.Add(entityBox, 1, 1);
        parserSection.Controls.Add(note, 1, 2);

        var modemSection = new TableLayoutPanel { AutoSize = true, Padding = new Padding(12), ColumnCount = 1, RowCount = 1, Dock = DockStyle.Fill };
        modemSection.Controls.Add(new Label { Text = "The modem COM port and SIM PIN are managed in dbo.txGSMSettings. Use Test Modem in Diagnostics after connecting the modem.", AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Color.DimGray }, 0, 0);

        var printerSection = new TableLayoutPanel { AutoSize = true, Padding = new Padding(12), ColumnCount = 1, RowCount = 1, Dock = DockStyle.Fill };
        printerSection.Controls.Add(new Label { Text = "Receipt printer settings are managed in dbo.txGSMSettings (PrintReceipt, PrinterName, and Copies). Payments are saved even if printing fails.", AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Color.DimGray }, 0, 0);

        var diagnosticsSection = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
        diagnosticsSection.Controls.AddRange(new Control[] { testModemButton, openLogsButton, dummyButton });

        GroupBox Section(string title, Control content) => new() { Text = title, Width = 540, AutoSize = true, Padding = new Padding(8), Controls = { content } };
        var sections = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        sections.Controls.AddRange(new Control[]
        {
            Section("Database Connection", databaseSection),
            Section("Parser Template and Entity", parserSection),
            Section("Modem and SIM", modemSection),
            Section("Receipt Printer", printerSection),
            Section("Diagnostics", diagnosticsSection)
        });
        settingsPanel.Controls.Add(sections);
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
                    var modemConnection = GsmModem.FindConnection(options with { PortName = configuredPort, SimPin = settings.Pin });
                    if (modemConnection is null)
                    {
                        var detectedPorts = SerialPort.GetPortNames();
                        var portSummary = detectedPorts.Length == 0 ? "no COM ports detected" : $"available: {string.Join(", ", detectedPorts)}";
                        BeginInvoke(() => SetStatus(string.IsNullOrWhiteSpace(configuredPort) ? $"Waiting: insert GSM modem ({portSummary})" : $"Waiting for modem on {configuredPort} ({portSummary})", Color.DarkGoldenrod));
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollingSeconds)), cancellationToken);
                        continue;
                    }
                    var portName = modemConnection.PortName;
                    if (!string.Equals(settings.PortName, portName, StringComparison.OrdinalIgnoreCase)) await database.SavePortNameAsync(portName, cancellationToken);
                    var modemOptions = options with { PortName = portName, BaudRate = modemConnection.BaudRate, SimPin = settings.Pin };
                    activePort = portName;
                    BeginInvoke(() => SetStatus($"Connected — pulling payments from {portName} at {modemConnection.BaudRate} baud", Color.FromArgb(31, 128, 74)));
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

    private void RequestStopPolling()
    {
        if (pollingTask is not { IsCompleted: false }) return;
        if (MessageBox.Show(this, "Stop pulling M-Pesa payments?", "Stop Puller", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            StopPolling();
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
                var connection = GsmModem.FindConnection(options with { PortName = settings.PortName ?? options.PortName, SimPin = settings.Pin });
                if (connection is null) return (PortName: (string?)null, BaudRate: (int?)null, Diagnostics: (ModemDiagnostics?)null);
                using var modem = new GsmModem(options with { PortName = connection.PortName, BaudRate = connection.BaudRate, SimPin = settings.Pin });
                modem.Open();
                var diagnostics = modem.ReadDiagnostics();
                if (!string.Equals(settings.PortName, connection.PortName, StringComparison.OrdinalIgnoreCase)) await database.SavePortNameAsync(connection.PortName, CancellationToken.None);
                return (PortName: (string?)connection.PortName, BaudRate: (int?)connection.BaudRate, Diagnostics: (ModemDiagnostics?)diagnostics);
            });
            modemLabel.Text = result.PortName is null ? "Modem: not found" : $"Modem: {result.PortName} at {result.BaudRate} baud | SIM {result.Diagnostics!.SimCard} | Signal {result.Diagnostics.Signal} | {result.Diagnostics.Network}";
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

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = ConfigPaths.LogsDirectory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Error("Opening the logs folder failed.", exception);
            MessageBox.Show(this, $"Could not open the logs folder:\n{ConfigPaths.LogsDirectory}", "Logs", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void MinimizeToTray()
    {
        trayIcon.Visible = true;
        Hide();
        if (pollingTask is { IsCompleted: false }) SetStatus("Running in background", Color.FromArgb(31, 128, 74));
        trayIcon.ShowBalloonTip(1500, "M-Pesa Puller", "Still running in the background", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        trayIcon.Visible = false;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (pollingTask is { IsCompleted: false }) SetStatus("Running", Color.FromArgb(31, 128, 74));
    }
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
