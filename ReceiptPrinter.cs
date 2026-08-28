using System.Drawing;
using System.Drawing.Printing;

static class ReceiptPrinter
{
    public static void Print(SmsMessage sms, GsmSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
        {
            throw new InvalidOperationException("PrintReceipt is enabled but PrinterName is empty in txGSMSettings.");
        }

        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = settings.PrinterName;
        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException($"Configured printer is not available: {settings.PrinterName}");
        }

        document.PrintPage += (_, eventArgs) =>
        {
            using var font = new Font("Consolas", 9);
            var lines = new[]
            {
                "M-PESA PAYMENT CONFIRMATION",
                new string('-', 32),
                $"Date:       {sms.Date:yyyy-MM-dd}",
                $"Time:       {sms.Time}",
                $"Transaction:{sms.TransactionNo ?? "Review"}",
                $"Amount:     Ksh {sms.Amount:N2}",
                $"Charges:    Ksh {sms.Charges:N2}",
                $"From:       {sms.SenderName}",
                $"Mobile:     {sms.MobileNo}",
                $"Reference:  {sms.BillNo ?? "-"}",
                $"Entity:     {settings.Entity ?? "-"}",
                $"Master Till:{settings.MasterTill ?? "-"}",
                new string('-', 32),
                "Payment received."
            };
            var y = 10f;
            foreach (var line in lines)
            {
                eventArgs.Graphics!.DrawString(line, font, Brushes.Black, 10, y);
                y += 18;
            }
        };

        document.PrinterSettings.Copies = (short)Math.Clamp(settings.Copies, 1, 10);
        document.Print();
    }
}
