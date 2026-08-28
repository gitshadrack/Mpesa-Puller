public sealed record PullerOptions
{
    public string PortName { get; init; } = "COM3";
    public string? SimPin { get; init; }
    public int BaudRate { get; init; } = 115200;
    public int PollingSeconds { get; init; } = 1;
    public bool DeleteAfterImport { get; init; } = true;
    public bool DiscardNonMpesaMessages { get; init; } = true;
    public int CommandTimeoutSeconds { get; init; } = 5;
}

public sealed record GsmSettings(string? PortName, string? Pin, bool CheckMessages, string? MasterTill, bool PrintReceipt, string? PrinterName, string? PrinterDriver, string? PrinterPort, int Copies, string? Entity);
public sealed record ModemDiagnostics(string SimCard, string Signal, string Network);
public sealed record SmsMessage(int Index, string ModemSender, string Body, string? TransactionNo, string MobileNo, string SenderName, DateTime Date, string Time, double Amount, double Charges, string? BillNo);
