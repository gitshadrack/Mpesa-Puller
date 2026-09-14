using Xunit;
using System;
using System.Collections.Generic;

public sealed class SmsParserTests
{
    [Fact]
    public void ParsesConfirmationFields()
    {
        var sms = SmsParser.Parse(1, "MPESA", "26/08/28", "18:30:00", "QK12AB34CD Confirmed. Ksh1,250.00 received from Jane Doe 254712345678 on 28/08/26 at 18:30. Transaction cost Ksh12.50. Bill 445566.");

        Assert.Equal("QK12AB34CD", sms.TransactionNo);
        Assert.Equal(1250, sms.Amount);
        Assert.Equal(12.5, sms.Charges);
        Assert.Equal("254712345678", sms.MobileNo);
        Assert.Equal("Jane Doe", sms.SenderName);
        Assert.Equal("445566", sms.BillNo);
        Assert.True(SmsParser.IsMpesa(sms));
        Assert.True(SmsParser.IsIncomingPayment(sms));
    }

    [Fact]
    public void KeepsUnparseableMessageForReview()
    {
        var sms = SmsParser.Parse(2, "MPESA", "26/08/28", "18:30:00", "Please check your account balance.");

        Assert.Null(sms.TransactionNo);
        Assert.Equal(0, sms.Amount);
        Assert.True(SmsParser.IsMpesa(sms));
        Assert.False(SmsParser.IsIncomingPayment(sms));
    }

    [Fact]
    public void ParsesCommonCmglHeaderWithEmptyField()
    {
        const string response = "\r\n+CMGL: 7,\"REC UNREAD\",\"+254712345678\",,\"26/08/28,18:30:00+12\"\r\nQK12AB34CD Confirmed. Ksh1,250.00 received from Jane Doe 254712345678.\r\n\r\nOK\r\n";
        var messages = GsmModem.ParseUnreadMessages(response);
        Assert.Single(messages);
        Assert.Equal("QK12AB34CD", messages[0].TransactionNo);
        Assert.Equal("+254712345678", messages[0].ModemSender);
    }

    [Fact]
    public void GroupsWrappedSmsAndCutsPromotionalTextAfterBalance()
    {
        const string response = "\r\n+CMGL: 16,\"REC UNREAD\",\"MPESA\",,\"26/08/28,22:44:00+12\"\r\nUHSNH45W4A Confirmed.You have received Ksh1.00 from DANIEL KAMAU 0715***897 on 28/8/26 at 10:44 PM\r\nNew M-PESA balance is Ksh1.00. Invest & earn daily i\r\n\r\nnterest with ZIIDI on https://saf.cx/cF6ir\r\n\r\nOK\r\n";

        var messages = GsmModem.ParseUnreadMessages(response);

        Assert.Single(messages);
        Assert.Equal("UHSNH45W4A", messages[0].TransactionNo);
        Assert.Contains("New M-PESA balance is Ksh1.00", SmsParser.TrimAfterBalance(messages[0].Body));
        Assert.DoesNotContain("ZIIDI", SmsParser.TrimAfterBalance(messages[0].Body));
    }

    [Fact]
    public void KeepsMaskedPhoneOutOfSenderName()
    {
        var sms = SmsParser.Parse(16, "MPESA", "26/08/28", "22:44:00", "UHSNH45W4A Confirmed.You have received Ksh1.00 from DANIEL KAMAU 0715***897 on 28/8/26 at 10:44 PM New M-PESA balance is Ksh1.00.");

        Assert.Equal("0715***897", sms.MobileNo);
        Assert.Equal("DANIEL KAMAU", sms.SenderName);
    }

    [Fact]
    public void ParsesTillMessageWithDirectPhoneNumber()
    {
        var sms = SmsParser.Parse(20, "MPESA", "26/08/29", "07:15:00", "QWE123RTY4 Confirmed. You have received Ksh 1,500.00 from JOHN DOE 254712345678 on 29/8/26 at 7:15 AM. New Till balance is Ksh 15,200.00.");

        Assert.Equal("QWE123RTY4", sms.TransactionNo);
        Assert.Equal(1500, sms.Amount);
        Assert.Equal("254712345678", sms.MobileNo);
        Assert.Equal("JOHN DOE", sms.SenderName);
        Assert.Contains("New Till balance is Ksh 15,200.00", SmsParser.TrimAfterBalance(sms.Body));
    }

    [Fact]
    public void ParsesWalletMessageWithParenthesizedPhoneAndInvoice()
    {
        var sms = SmsParser.Parse(21, "MPESA", "26/08/29", "08:30:00", "RTY456UIO7 Confirmed. Ksh 3,000.00 received from MARY ANNE (254798765432) for account/invoice 102030 on 29/8/26 at 8:30 AM. New wallet balance is Ksh 45,000.00.");

        Assert.Equal("RTY456UIO7", sms.TransactionNo);
        Assert.Equal(3000, sms.Amount);
        Assert.Equal("254798765432", sms.MobileNo);
        Assert.Equal("MARY ANNE", sms.SenderName);
        Assert.Equal("102030", sms.BillNo);
    }

    [Fact]
    public void ParsesBankOriginAndGenericBalanceMessage()
    {
        var sms = SmsParser.Parse(22, "MPESA", "26/08/29", "09:00:00", "UIO789PAS1 Confirmed. Ksh 750.00 received from PETER KAMAU (254722111222) via KCB on 29/8/26 at 9:00 AM. New balance is Ksh 12,450.0");

        Assert.Equal("UIO789PAS1", sms.TransactionNo);
        Assert.Equal(750, sms.Amount);
        Assert.Equal("254722111222", sms.MobileNo);
        Assert.Equal("PETER KAMAU", sms.SenderName);
        Assert.Contains("New balance is Ksh 12,450.0", SmsParser.TrimAfterBalance(sms.Body));
    }

    [Theory]
    [InlineData("QWE123RTY4 Confirmed. Ksh 1,500.00 sent to JOHN DOE 254712345678. New M-PESA balance is Ksh 15,200.00.")]
    [InlineData("QWE123RTY4 Confirmed. Ksh 1,500.00 has been reversed. New M-PESA balance is Ksh 15,200.00.")]
    [InlineData("Your M-PESA balance is Ksh 15,200.00.")]
    public void DoesNotTreatNonPaymentMessagesAsIncomingPayments(string body)
    {
        var sms = SmsParser.Parse(23, "MPESA", "26/08/29", "09:00:00", body);

        Assert.False(SmsParser.IsIncomingPayment(sms));
    }

    [Fact]
    public void UsesSelectedProviderProfileToFilterPayments()
    {
        var kcbPayment = SmsParser.Parse(24, "MPESA", "26/08/29", "09:00:00", "UIO789PAS1 Confirmed. Ksh 750.00 received from PETER KAMAU 254722111222 via KCB on 29/8/26 at 9:00 AM.");

        Assert.True(SmsParser.IsIncomingPayment(kcbPayment, MessageProfiles.Kcb));
        Assert.False(SmsParser.IsIncomingPayment(kcbPayment, MessageProfiles.Equity));
    }

    [Fact]
    public void UsesSelectedTillProfileToFilterPayments()
    {
        var tillPayment = SmsParser.Parse(25, "MPESA", "26/08/29", "09:00:00", "QWE123RTY4 Confirmed. You have received Ksh 1,500.00 from JOHN DOE 254712345678. New Till balance is Ksh 15,200.00.");

        Assert.True(SmsParser.IsIncomingPayment(tillPayment, MessageProfiles.SafaricomTill));
        Assert.False(SmsParser.IsIncomingPayment(tillPayment, MessageProfiles.PochiLaBiashara));
    }

    [Theory]
    [InlineData(MessageProfiles.SafaricomTill, "QWE123RTY4 Confirmed. You have received Ksh1,500.00 from JOHN DOE (254722***000) on 29/8/26 at 7:15 AM. New Till balance is Ksh15,200.00.")]
    [InlineData(MessageProfiles.KopoKopo, "RTY456UIO7 Confirmed. You have received Ksh850.00 from MARY ANNE (254711***111) on 29/8/26 at 8:12 AM. New Till balance is Ksh4,850.00.")]
    [InlineData(MessageProfiles.SafaricomPaybill, "PAS321GHJ8 Confirmed. You have received Ksh5,000.00 from ALICE CHEROTICH (254799***333) for account ACC-9900 on 29/8/26 at 10:15 AM. New Utility balance is Ksh120,400.00.")]
    [InlineData(MessageProfiles.Equity, "DFG567HJK9 Confirmed. Ksh2,300.00 received from DAVID OMONDI (254712***444) via Equity Merchant on 29/8/26 at 11:30 AM. New Account balance is Ksh67,900.00.")]
    [InlineData(MessageProfiles.Kcb, "ZXC987VBN2 Confirmed. Ksh1,200.00 received from GRACE MUTUA (254723***555) via KCB Bank on 29/8/26 at 12:45 PM. New business balance is Ksh34,150.00.")]
    [InlineData(MessageProfiles.Dtb, "POI098UYT6 Confirmed. Ksh4,500.00 received from EVANS KIPROP (254704***666) via DTB Merchant on 29/8/26 at 1:15 PM. New wallet balance is Ksh92,300.00.")]
    [InlineData(MessageProfiles.NationalBank, "LKJ765MNB3 Confirmed. Ksh3,150.00 received from AGNES WANJIKU (254755***777) via National Bank on 29/8/26 at 2:20 PM. New balance is Ksh41,800.00.")]
    public void AcceptsSuppliedEnglishMerchantTemplates(string profile, string body)
    {
        var sms = SmsParser.Parse(26, "MPESA", "26/08/29", "09:00:00", body, profile);

        Assert.True(SmsParser.IsIncomingPayment(sms, profile));
    }

    [Fact]
    public void AcceptsSuppliedPochiLaBiasharaTemplate()
    {
        var sms = SmsParser.Parse(27, "MPESA", "26/08/29", "09:00:00", "UIO789PAS1 Umepokea Ksh700.00 kutoka kwa PETER KAMAU (254700***222) kwny pochi la biashara yako on 29/8/26 at 9:02 AM. Salio jipya la Pochi ni Ksh3,400.00.", MessageProfiles.PochiLaBiashara);

        Assert.Equal("UIO789PAS1", sms.TransactionNo);
        Assert.Equal("PETER KAMAU", sms.SenderName);
        Assert.True(SmsParser.IsIncomingPayment(sms, MessageProfiles.PochiLaBiashara));
    }

    [Fact]
    public void ParsesKcbPaybillCompletedTemplate()
    {
        const string body = "UI1I75S7WJ completed. You have received KES 110 from DANIEL WAMBUA 254721683483 for account SERENGETIHOTEL 7723635 on 01/09/2026 at 03:31 PM. KCB Go Ahead.";

        var sms = SmsParser.Parse(28, "KCB", "26/09/01", "15:31:00", body, MessageProfiles.KcbPaybill);

        Assert.Equal("UI1I75S7WJ", sms.TransactionNo);
        Assert.Equal(110, sms.Amount);
        Assert.Equal("254721683483", sms.MobileNo);
        Assert.Equal("DANIEL WAMBUA", sms.SenderName);
        Assert.Equal("SERENGETIHOTEL 7723635", sms.BillNo);
        Assert.True(SmsParser.IsIncomingPayment(sms, MessageProfiles.KcbPaybill));
        Assert.False(SmsParser.IsIncomingPayment(sms, MessageProfiles.KcbTillNumber));
    }

    [Theory]
    [InlineData("Generic 58mm Thermal", 315, 58)]
    [InlineData("Generic 80mm Thermal", 228, 80)]
    [InlineData(null, 228, 58)]
    [InlineData(null, 315, 80)]
    public void SelectsThermalReceiptWidth(string? driver, int detectedWidth, int expectedWidth)
    {
        Assert.Equal(expectedWidth, ReceiptPrinter.ResolvePaperWidthMm(driver, detectedWidth));
    }

    [Fact]
    public void BuildsApprovedPaymentReceiptWithoutMerchantCopy()
    {
        const string body = "UI1I75S7WJ completed. You have received KES 110 from DANIEL WAMBUA 254721683483 for account SERENGETIHOTEL 7723635.";
        var sms = SmsParser.Parse(29, "KCB", "26/09/01", "15:31:00", body, MessageProfiles.KcbPaybill);
        var settings = new GsmSettings("COM3", null, true, "522522", true, "Thermal", "58mm", "USB", 1, "SERENGETI HOTEL");
        var lines = ReceiptPrinter.BuildReceiptLines(sms, settings, 58);
        var receipt = string.Join(" ", lines);

        Assert.Equal("SERENGETI HOTEL", lines[0]);
        Assert.Equal("PAYMENT APPROVED", lines[2]);
        Assert.Contains("KES 110.00", lines);
        Assert.Contains("UI1I75S7WJ", receipt);
        Assert.Contains("DANIEL WAMBUA", receipt);
        Assert.Contains("DATE: 01-09-2026 15:31", receipt);
        Assert.Contains("CASHIER: Cashier", receipt);
        Assert.DoesNotContain("MERCHANT", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COPY", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("You have received", receipt);
        Assert.All(lines, line => Assert.True(line.Length <= 30));
    }
}
