using Xunit;
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
    }

    [Fact]
    public void KeepsUnparseableMessageForReview()
    {
        var sms = SmsParser.Parse(2, "MPESA", "26/08/28", "18:30:00", "Please check your account balance.");

        Assert.Null(sms.TransactionNo);
        Assert.Equal(0, sms.Amount);
        Assert.True(SmsParser.IsMpesa(sms));
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
}
