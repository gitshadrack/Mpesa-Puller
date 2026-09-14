using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

sealed class DatabaseWriter
{
    private string connectionString;
    private readonly ILogger<DatabaseWriter> logger;

    public DatabaseWriter(IConfiguration configuration, ILogger<DatabaseWriter> logger)
    {
        connectionString = configuration.GetConnectionString("Restaurant") ?? throw new InvalidOperationException("ConnectionStrings:Restaurant is required.");
        this.logger = logger;
    }

    public async Task<GsmSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT TOP (1) PortNo, PIN, CheckMessages, MasterTill, PrintReceipt, PrinterName, PrinterDriver, PrinterPort, Copies, Entity FROM dbo.txGSMSettings;";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new GsmSettings(null, null, true, null, false, null, null, null, 1, null);
        var portName = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var pin = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        var checkMessages = reader.IsDBNull(2) || IsEnabled(reader.GetValue(2));
        var masterTill = ReadText(reader, 3);
        var printReceipt = !reader.IsDBNull(4) && IsEnabled(reader.GetValue(4));
        var printerName = ReadText(reader, 5);
        var printerDriver = ReadText(reader, 6);
        var printerPort = ReadText(reader, 7);
        var copies = reader.IsDBNull(8) ? 1 : Math.Clamp(Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture), 1, 10);
        var entity = ReadText(reader, 9);
        return new GsmSettings(string.IsNullOrWhiteSpace(portName) ? null : portName, string.IsNullOrWhiteSpace(pin) ? null : pin, checkMessages, masterTill, printReceipt, printerName, printerDriver, printerPort, copies, entity);
    }

    public async Task SavePortNameAsync(string portName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string updateSql = "UPDATE TOP (1) dbo.txGSMSettings SET PortNo = @PortNo;";
        await using var updateCommand = new SqlCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue("@PortNo", portName);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            const string insertSql = "INSERT INTO dbo.txGSMSettings (PortNo, CheckMessages) VALUES (@PortNo, N'1');";
            await using var insertCommand = new SqlCommand(insertSql, connection);
            insertCommand.Parameters.AddWithValue("@PortNo", portName);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<int> InsertDummyMessagesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var messages = Enumerable.Range(1, 3).Select(number =>
        {
            var body = $"QA{now:MMddHHmmss}{number:D2} Confirmed. You have received Ksh{number * 1000:N2} from Demo Customer 25470000000 on {now:dd/MM/yy} at {now:HH:mm}.";
            return new SmsMessage(-number, "MPESA", body, $"QA{now:MMddHHmmss}{number:D2}", "25470000000", "Demo Customer", now.Date, now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), number * 1000, 0, null);
        });
        var inserted = 0;
        foreach (var message in messages) if (await ImportAsync(message, cancellationToken)) inserted++;
        return inserted;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public static async Task TestConnectionAsync(string candidateConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(candidateConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public void UpdateConnectionString(string value) => Volatile.Write(ref connectionString, value);

    public async Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT
                (SELECT COUNT(1) FROM dbo.txMessages WHERE [Date] = @Today) AS PaymentCount,
                (SELECT COALESCE(SUM(Amount), 0) FROM dbo.txMessages WHERE [Date] = @Today) AS TotalAmount,
                (SELECT TOP (1) TransactionNo FROM dbo.txMessages ORDER BY TransNo DESC) AS TransactionNo,
                (SELECT TOP (1) SenderName FROM dbo.txMessages ORDER BY TransNo DESC) AS SenderName,
                (SELECT TOP (1) Amount FROM dbo.txMessages ORDER BY TransNo DESC) AS Amount,
                (SELECT TOP (1) [Time] FROM dbo.txMessages ORDER BY TransNo DESC) AS [Time];
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Today", DateTime.Today);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new DashboardSummary(0, 0, null, null, null, null);
        return new DashboardSummary(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
            ReadText(reader, 2),
            ReadText(reader, 3),
            reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
            ReadText(reader, 5));
    }

    public async Task<IReadOnlyList<SmsMessage>> GetMessagesByDateAsync(DateTime date, int maximumRows, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT TOP (@MaximumRows) TransactionNo, MobileNo, [Date], [Time], Amount, Charges, SenderName, RawMessage, BillNo, NetworkName FROM dbo.txMessages WHERE [Date] = @Date ORDER BY TransNo DESC;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@MaximumRows", Math.Clamp(maximumRows, 1, 2000));
        command.Parameters.AddWithValue("@Date", date.Date);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<SmsMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new SmsMessage(
                0,
                ReadText(reader, 9) ?? "MPESA",
                ReadText(reader, 7) ?? string.Empty,
                ReadText(reader, 0),
                ReadText(reader, 1) ?? string.Empty,
                ReadText(reader, 6) ?? "Unknown",
                reader.IsDBNull(2) ? DateTime.Today : reader.GetDateTime(2),
                ReadText(reader, 3) ?? "00:00:00",
                reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? 0 : Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture),
                ReadText(reader, 8)));
        }
        return messages;
    }

    public async Task<bool> ImportAsync(SmsMessage sms, GsmSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string duplicateSql = "SELECT COUNT_BIG(1) FROM dbo.txMessagesRaw WITH (UPDLOCK, HOLDLOCK) WHERE Message = @Message OR (@TransactionNo IS NOT NULL AND @TransactionNo <> N'' AND EXISTS (SELECT 1 FROM dbo.txMessages WITH (UPDLOCK, HOLDLOCK) WHERE TransactionNo = @TransactionNo));";
        await using (var duplicateCommand = new SqlCommand(duplicateSql, connection, transaction))
        {
            duplicateCommand.Parameters.AddWithValue("@Message", sms.Body);
            duplicateCommand.Parameters.AddWithValue("@TransactionNo", (object?)Fit(sms.TransactionNo, 100) ?? DBNull.Value);
            if ((long)(await duplicateCommand.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        }
        const string rawSql = "INSERT INTO dbo.txMessagesRaw ([Date], [Time], [Message]) VALUES (@Date, @Time, @Message);";
        await using (var rawCommand = new SqlCommand(rawSql, connection, transaction))
        {
            rawCommand.Parameters.AddWithValue("@Date", sms.Date);
            rawCommand.Parameters.AddWithValue("@Time", sms.Time);
            rawCommand.Parameters.AddWithValue("@Message", sms.Body);
            await rawCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        const string messageSql = "INSERT INTO dbo.txMessages (TransactionNo, MobileNo, [Date], [Time], Amount, Charges, AmountUsed, SenderName, RawMessage, Status, Amount1, BillNo, CashierName, NetworkName, StatusDate, StatusTime, Notes) VALUES (@TransactionNo, @MobileNo, @Date, @Time, @Amount, @Charges, 0, @SenderName, @RawMessage, @Status, @Amount, @BillNo, NULL, N'M-Pesa', @StatusDate, CAST(@StatusTime AS time), @Notes);";
        await using (var messageCommand = new SqlCommand(messageSql, connection, transaction))
        {
            messageCommand.Parameters.AddWithValue("@TransactionNo", (object?)Fit(sms.TransactionNo, 100) ?? DBNull.Value);
            messageCommand.Parameters.AddWithValue("@MobileNo", (object?)Fit(sms.MobileNo, 500) ?? DBNull.Value);
            messageCommand.Parameters.AddWithValue("@Date", sms.Date);
            messageCommand.Parameters.AddWithValue("@Time", sms.Time);
            messageCommand.Parameters.AddWithValue("@Amount", sms.Amount);
            messageCommand.Parameters.AddWithValue("@Charges", sms.Charges);
            messageCommand.Parameters.AddWithValue("@SenderName", Fit(sms.SenderName, 500));
            messageCommand.Parameters.AddWithValue("@RawMessage", Fit(SmsParser.TrimAfterBalance(sms.Body), 3000));
            messageCommand.Parameters.AddWithValue("@Status", sms.TransactionNo is null ? "Review" : "Pending");
            messageCommand.Parameters.AddWithValue("@BillNo", Fit(sms.BillNo, 500) ?? (object)DBNull.Value);
            messageCommand.Parameters.AddWithValue("@StatusDate", DateTime.Now.Date);
            messageCommand.Parameters.AddWithValue("@StatusTime", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            var notes = sms.TransactionNo is null ? "SMS could not be parsed automatically." : "Imported from GSM modem.";
            if (sms.Body.Length > 3000) notes += " RawMessage contains the first 3000 characters; the complete SMS is in txMessagesRaw.";
            messageCommand.Parameters.AddWithValue("@Notes", notes);
            await messageCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("Stored SMS from {Sender}.", sms.ModemSender);
        return true;
    }

    public async Task<bool> ImportAsync(SmsMessage sms, CancellationToken cancellationToken) =>
        await ImportAsync(sms, new GsmSettings(null, null, true, null, false, null, null, null, 1, null), cancellationToken);

    private static string? ReadText(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)?.Trim();

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var connection = new SqlConnection(Volatile.Read(ref connectionString));
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (Exception exception) when (exception is SqlException or TimeoutException)
            {
                lastException = exception;
                AppLog.Error("SQL connection attempt failed.", exception, $"Attempt: {attempt} of 3");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
        AppLog.Error("SQL Server connection failed after all retry attempts.", lastException!, "Attempts: 3");
        throw new InvalidOperationException("Could not connect to SQL Server after 3 attempts.", lastException);
    }

    private static string? Fit(string? value, int maxLength) => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
    private static bool IsEnabled(object value) => value switch
    {
        bool boolean => boolean,
        byte or short or int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        _ => bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var result) ? result : Convert.ToString(value, CultureInfo.InvariantCulture) == "1"
    };
}
