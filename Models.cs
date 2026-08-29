public sealed record PullerOptions
{
    public string PortName { get; init; } = "COM3";
    public string? SimPin { get; init; }
    public int BaudRate { get; init; } = 115200;
    public int PollingSeconds { get; init; } = 1;
    public bool DeleteAfterImport { get; init; } = true;
    public bool DiscardNonMpesaMessages { get; init; } = true;
    public int CommandTimeoutSeconds { get; init; } = 5;
    public string MessageProfile { get; init; } = "Auto Detect";
}

public static class MessageProfiles
{
    public const string AutoDetect = "Auto Detect";
    public const string SafaricomTill = "Safaricom Till";
    public const string SafaricomPaybill = "Safaricom Paybill";
    public const string PochiLaBiashara = "Pochi la Biashara";
    public const string Equity = "Equity";
    public const string KcbTillNumber = "KCB Till Number";
    public const string KcbPaybill = "KCB Paybill";
    public const string KopoKopoTillNumber = "Kopo Kopo Till Number";
    public const string KopoKopoPaybill = "Kopo Kopo Paybill";
    // Compatibility names for existing configurations and integrations.
    public const string Kcb = KcbTillNumber;
    public const string KopoKopo = KopoKopoTillNumber;
    public const string Dtb = "DTB";
    public const string NationalBank = "National Bank";

    public static readonly string[] Names =
    [
        AutoDetect, SafaricomTill, SafaricomPaybill, PochiLaBiashara,
        Equity, KcbTillNumber, KcbPaybill, KopoKopoTillNumber, KopoKopoPaybill, Dtb, NationalBank
    ];

    public static readonly string[] Formats =
    [
        AutoDetect, SafaricomTill, SafaricomPaybill, PochiLaBiashara,
        Equity, "KCB", "Kopo Kopo", Dtb, NationalBank
    ];

    public static string Normalize(string? value)
    {
        if (string.Equals(value?.Trim(), "KCB", StringComparison.OrdinalIgnoreCase)) return KcbTillNumber;
        if (string.Equals(value?.Trim(), "Kopo Kopo", StringComparison.OrdinalIgnoreCase)) return KopoKopoTillNumber;
        return Names.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Names.First(name => string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            : AutoDetect;
    }

    public static string EntityLabel(string? value) => Normalize(value) switch
    {
        PochiLaBiashara => "Personal (Pochi la Biashara)",
        _ => Normalize(value)
    };

    public static string FormatFor(string? value) => Normalize(value) switch
    {
        KcbTillNumber or KcbPaybill => "KCB",
        KopoKopoTillNumber or KopoKopoPaybill => "Kopo Kopo",
        _ => Normalize(value)
    };

    public static IReadOnlyList<string> EntityOptions(string? format) => FormatFor(format) switch
    {
        "KCB" => [KcbTillNumber, KcbPaybill],
        "Kopo Kopo" => [KopoKopoTillNumber, KopoKopoPaybill],
        PochiLaBiashara => ["Personal"],
        SafaricomTill => ["Till Number"],
        SafaricomPaybill => ["Paybill"],
        _ => [EntityLabel(format)]
    };

    public static string ProfileFor(string? format, string? entity) => FormatFor(format) switch
    {
        "KCB" when string.Equals(entity, KcbPaybill, StringComparison.OrdinalIgnoreCase) => KcbPaybill,
        "KCB" => KcbTillNumber,
        "Kopo Kopo" when string.Equals(entity, KopoKopoPaybill, StringComparison.OrdinalIgnoreCase) => KopoKopoPaybill,
        "Kopo Kopo" => KopoKopoTillNumber,
        _ => Normalize(format)
    };
}

public sealed record GsmSettings(string? PortName, string? Pin, bool CheckMessages, string? MasterTill, bool PrintReceipt, string? PrinterName, string? PrinterDriver, string? PrinterPort, int Copies, string? Entity);
public sealed record ModemDiagnostics(string SimCard, string Signal, string Network);
public sealed record SmsMessage(int Index, string ModemSender, string Body, string? TransactionNo, string MobileNo, string SenderName, DateTime Date, string Time, double Amount, double Charges, string? BillNo);
public sealed record DashboardSummary(int PaymentCount, double TotalAmount, string? LastTransactionNo, string? LastSenderName, double? LastAmount, string? LastTime);
