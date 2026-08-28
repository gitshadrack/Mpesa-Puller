using System.Globalization;
using System.Text.RegularExpressions;

static class NetworkNames
{
    private static readonly Dictionary<string, string> KenyaOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["63902"] = "Safaricom",
        ["63903"] = "Airtel",
        ["63905"] = "Equitel",
        ["63907"] = "Telkom Kenya",
        ["63910"] = "Airtel"
    };

    public static string Resolve(string value) => KenyaOperators.TryGetValue(value, out var name) ? name : value;
}

public static class SmsParser
{
    private static readonly Regex TransactionPattern = new(@"\b([A-Z0-9]{8,})\s+Confirmed\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmountPattern = new(@"\b(?:Ksh|KES)\s*([\d,]+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChargesPattern = new(@"(?:charge|fee|cost)[^\d]{0,20}(?:Ksh|KES)?\s*([\d,]+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MobilePattern = new(@"(?<!\d)(?:254\d{2,9}\*+\d{3}|0\d{3}\*+\d{3}|254\d{9}|0\d{9})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SenderPattern = new(@"\bfrom\s+(.+?)(?:\s+(?:254\d{2,9}\*+\d{3}|0\d{3}\*+\d{3}|254\d{9}|0\d{9})\b|\s+(?:on|at)\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BillPattern = new(@"\b(?:bill|account|ref(?:erence)?)[\s:#-]*([A-Z0-9][A-Z0-9/-]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BalanceEndPattern = new(@"\b(?:New\s+)?M[- ]?PESA\s+balance\s+is\s+(?:(?:Ksh|KES)\s*)?[\d,]+(?:\.\d{1,2})?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsMpesa(SmsMessage sms) =>
        sms.ModemSender.Contains("MPESA", StringComparison.OrdinalIgnoreCase) ||
        sms.ModemSender.Contains("M-PESA", StringComparison.OrdinalIgnoreCase) ||
        (sms.TransactionNo is not null && AmountPattern.IsMatch(sms.Body));

    public static SmsMessage Parse(int index, string sender, string modemDate, string modemTime, string body)
    {
        var transactionBody = TrimAfterBalance(body);
        var transaction = TransactionPattern.Match(transactionBody).Groups[1].Value;
        var amountText = AmountPattern.Match(transactionBody).Groups[1].Value.Replace(",", string.Empty);
        double.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount);
        var mobile = MobilePattern.Match(transactionBody).Value;
        var senderName = SenderPattern.Match(transactionBody).Groups[1].Value.Trim();
        if (senderName.Length == 0)
        {
            senderName = sender;
        }

        DateTime.TryParseExact(modemDate + "," + modemTime, "yy/MM/dd,HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime);
        if (dateTime == default)
        {
            dateTime = DateTime.Now;
        }

        var chargesText = ChargesPattern.Match(transactionBody).Groups[1].Value.Replace(",", string.Empty);
        double.TryParse(chargesText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var charges);
        var billNo = BillPattern.Match(transactionBody).Groups[1].Value;
        return new SmsMessage(index, sender, body, transaction.Length == 0 ? null : transaction, mobile, senderName, dateTime.Date, dateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), amount, charges, billNo.Length == 0 ? null : billNo);
    }

    public static string TrimAfterBalance(string body)
    {
        var balanceMatch = BalanceEndPattern.Match(body);
        return balanceMatch.Success ? body[..(balanceMatch.Index + balanceMatch.Length)] : body;
    }
}
