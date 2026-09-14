using System.Drawing;
using System.Drawing.Printing;

public static class ReceiptPrinter
{
    private const int FiftyEightMm = 58;
    private const int EightyMm = 80;

    public static void Print(SmsMessage sms, GsmSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            throw new InvalidOperationException("PrintReceipt is enabled but PrinterName is empty in txGSMSettings.");

        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = settings.PrinterName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Configured printer is not available: {settings.PrinterName}");

        var widthMm = ResolvePaperWidthMm(settings.PrinterDriver, document.DefaultPageSettings.PaperSize.Width);
        var paperWidth = (int)Math.Round(widthMm / 25.4 * 100);
        var lines = BuildReceiptLines(sms, settings, widthMm);
        var paperHeight = Math.Max(250, 35 + lines.Count * 17);
        document.DefaultPageSettings.Margins = new Margins(5, 5, 5, 5);
        document.DefaultPageSettings.PaperSize = new PaperSize($"M-Pesa {widthMm}mm", paperWidth, paperHeight);
        document.OriginAtMargins = true;

        document.PrintPage += (_, eventArgs) =>
        {
            var graphics = eventArgs.Graphics ?? throw new InvalidOperationException("The printer did not provide a graphics surface.");
            using var normalFont = new Font("Consolas", widthMm == FiftyEightMm ? 8f : 9f);
            using var headingFont = new Font("Consolas", widthMm == FiftyEightMm ? 10f : 12f, FontStyle.Bold);
            using var amountFont = new Font("Consolas", widthMm == FiftyEightMm ? 9f : 11f, FontStyle.Bold);
            using var centered = new StringFormat { Alignment = StringAlignment.Center };
            var y = 0f;
            var centerSection = true;
            var entitySection = !string.IsNullOrWhiteSpace(settings.Entity);
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    centerSection = false;
                    y += normalFont.GetHeight(graphics) / 2;
                    continue;
                }
                var isDivider = line.All(character => character == '-');
                var font = entitySection && !isDivider
                    ? headingFont
                    : line == "PAYMENT APPROVED"
                        ? headingFont
                        : line.StartsWith("KES ", StringComparison.Ordinal)
                            ? amountFont
                            : normalFont;
                if (centerSection)
                    graphics.DrawString(line, font, Brushes.Black, new RectangleF(0, y, eventArgs.MarginBounds.Width, font.GetHeight(graphics) + 4), centered);
                else
                    graphics.DrawString(line, font, Brushes.Black, 0, y);
                y += font.GetHeight(graphics) + 2;
                if (entitySection && isDivider) entitySection = false;
            }
            eventArgs.HasMorePages = false;
        };

        document.PrinterSettings.Copies = (short)Math.Clamp(settings.Copies, 1, 10);
        document.Print();
    }

    public static int ResolvePaperWidthMm(string? printerDriver, int paperWidthInHundredthsOfInch)
    {
        if (printerDriver?.Contains("58", StringComparison.OrdinalIgnoreCase) == true) return FiftyEightMm;
        if (printerDriver?.Contains("80", StringComparison.OrdinalIgnoreCase) == true) return EightyMm;
        var detectedMm = paperWidthInHundredthsOfInch / 100d * 25.4;
        return detectedMm > 0 && detectedMm < 70 ? FiftyEightMm : EightyMm;
    }

    public static IReadOnlyList<string> BuildReceiptLines(SmsMessage sms, GsmSettings settings, int widthMm)
    {
        var charactersPerLine = widthMm == FiftyEightMm ? 30 : 42;
        var divider = new string('-', charactersPerLine);
        var transactionNo = sms.TransactionNo ?? "REVIEW";
        var receiptDate = sms.Date;
        if (TimeSpan.TryParse(sms.Time, out var receiptTime)) receiptDate = receiptDate.Add(receiptTime);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.Entity))
        {
            lines.AddRange(Wrap(settings.Entity.Trim().ToUpperInvariant(), charactersPerLine));
            lines.Add(divider);
        }
        lines.Add("PAYMENT APPROVED");
        lines.Add($"KES {sms.Amount:N2}");
        lines.Add($"{transactionNo} MPESA");
        lines.AddRange(Wrap(sms.SenderName.ToUpperInvariant(), charactersPerLine));
        lines.Add(string.Empty);
        lines.Add($"DATE: {receiptDate:dd-MM-yyyy HH:mm}");
        lines.Add("CASHIER: Cashier");
        lines.AddRange(Wrap($"RECEIPT NO: {transactionNo}", charactersPerLine));
        lines.Add(divider);
        lines.Add(string.Empty);
        return lines;
    }

    public static IReadOnlyList<string> Wrap(string text, int maximumLength)
    {
        var lines = new List<string>();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var word in words)
        {
            if (word.Length > maximumLength)
            {
                if (current.Length > 0) { lines.Add(current); current = string.Empty; }
                for (var offset = 0; offset < word.Length; offset += maximumLength)
                    lines.Add(word.Substring(offset, Math.Min(maximumLength, word.Length - offset)));
                continue;
            }
            if (current.Length == 0) current = word;
            else if (current.Length + 1 + word.Length <= maximumLength) current += " " + word;
            else { lines.Add(current); current = word; }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }
}
