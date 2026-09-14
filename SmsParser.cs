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
    private static readonly Regex TransactionPattern = new(@"\b([A-Z0-9]{8,})\s+(?:Confirmed|completed)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingTransactionPattern = new(@"^\s*([A-Z0-9]{8,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmountPattern = new(@"\b(?:Ksh|KES)\s*([\d,]+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChargesPattern = new(@"(?:charge|fee|cost)[^\d]{0,20}(?:Ksh|KES)?\s*([\d,]+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MobilePattern = new(@"(?<!\d)(?:254\d{2,9}\*+\d{3}|0\d{3}\*+\d{3}|254\d{9}|0\d{9})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SenderPattern = new(@"\b(?:from|kutoka\s+kwa)\s+(.+?)(?:\s*\(?\s*(?:254\d{2,9}\*+\d{3}|0\d{3}\*+\d{3}|254\d{9}|0\d{9})\s*\)?|\s+(?:on|at|kwny)\s+|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BillPattern = new(@"\b(?:bill|account(?:/invoice)?|invoice|ref(?:erence)?)[\s:#/-]+([A-Z0-9][A-Z0-9/-]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KcbPaybillAccountPattern = new(@"\bfor\s+account\s+(.+?)\s+on\s+\d{1,2}/\d{1,2}/\d{2,4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BalanceEndPattern = new(@"\b(?:New\s+)?(?:(?:M[- ]?PESA|Till|wallet|account|business|utility)\s+)?balance\s+is\s+(?:(?:Ksh|KES)\s*)?[\d,]+(?:\.\d{1,2})?|\bSalio\s+jipya\s+la\s+Pochi\s+ni\s+(?:(?:Ksh|KES)\s*)?[\d,]+(?:\.\d{1,2})?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingPaymentPattern = new(@"\b(?:you\s+have\s+)?received\s+(?:Ksh|KES)\s*[\d,]+(?:\.\d{1,2})?\s+from\b|\b(?:Ksh|KES)\s*[\d,]+(?:\.\d{1,2})?\s+received\s+from\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PochiPaymentPattern = new(@"\bumepokea\s+(?:Ksh|KES)\s*[\d,]+(?:\.\d{1,2})?\s+kutoka\s+kwa\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsMpesa(SmsMessage sms) =>
        sms.ModemSender.Contains("MPESA", StringComparison.OrdinalIgnoreCase) ||
        sms.ModemSender.Contains("M-PESA", StringComparison.OrdinalIgnoreCase) ||
        (sms.TransactionNo is not null && AmountPattern.IsMatch(sms.Body));

    // Only confirmed incoming payments are eligible for database import. This excludes
    // balance alerts, outgoing payments, reversals, failed transactions, and promotions.
    public static bool IsIncomingPayment(SmsMessage sms, string? messageProfile = null)
    {
        var profile = MessageProfiles.Normalize(messageProfile);
        var body = TrimAfterBalance(sms.Body);
        var isPaymentWording = IncomingPaymentPattern.IsMatch(body) ||
                               (profile == MessageProfiles.PochiLaBiashara && PochiPaymentPattern.IsMatch(body));
        return sms.TransactionNo is not null && sms.Amount > 0 && isPaymentWording && MatchesProfile(body, profile);
    }

    private static bool MatchesProfile(string body, string? messageProfile)
    {
        var profile = MessageProfiles.Normalize(messageProfile);
        if (profile == MessageProfiles.AutoDetect) return true;

        return profile switch
        {
            MessageProfiles.SafaricomTill => ContainsAny(body, "new till balance"),
            // The supplied Kopo Kopo receipt has the Safaricom Till wording and no
            // separate Kopo identifier; the configured profile identifies this channel.
            MessageProfiles.KopoKopoTillNumber or MessageProfiles.KopoKopoPaybill => ContainsAny(body, "new till balance"),
            MessageProfiles.SafaricomPaybill => ContainsAny(body, "for account") && ContainsAny(body, "new utility balance", "new mmf balance"),
            MessageProfiles.PochiLaBiashara => ContainsAny(body, "kwny pochi la biashara"),
            MessageProfiles.Equity => ContainsAny(body, "equity"),
            MessageProfiles.KcbTillNumber => ContainsAny(body, "kcb") && !ContainsAny(body, "for account"),
            MessageProfiles.KcbPaybill => ContainsAny(body, "kcb") && ContainsAny(body, "for account"),
            MessageProfiles.Dtb => ContainsAny(body, "dtb", "diamond trust"),
            MessageProfiles.NationalBank => ContainsAny(body, "national bank", "nationalbank"),
            _ => false
        };
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    public static SmsMessage Parse(int index, string sender, string modemDate, string modemTime, string body, string messageProfile = "Auto Detect")
    {
        var transactionBody = TrimAfterBalance(body);
        var transaction = TransactionPattern.Match(transactionBody).Groups[1].Value;
        if (transaction.Length == 0 && MessageProfiles.Normalize(messageProfile) == MessageProfiles.PochiLaBiashara)
        {
            transaction = LeadingTransactionPattern.Match(transactionBody).Groups[1].Value;
        }
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
        var profile = MessageProfiles.Normalize(messageProfile);
        var billNo = profile == MessageProfiles.KcbPaybill
            ? KcbPaybillAccountPattern.Match(transactionBody).Groups[1].Value.Trim()
            : BillPattern.Match(transactionBody).Groups[1].Value;
        return new SmsMessage(index, sender, body, transaction.Length == 0 ? null : transaction, mobile, senderName, dateTime.Date, dateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), amount, charges, billNo.Length == 0 ? null : billNo);
    }

    public static string TrimAfterBalance(string body)
    {
        var balanceMatch = BalanceEndPattern.Match(body);
        return balanceMatch.Success ? body[..(balanceMatch.Index + balanceMatch.Length)] : body;
    }
}
