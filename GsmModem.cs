using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

public sealed class GsmModem : IDisposable
{
    private readonly PullerOptions options;
    private readonly SerialPort port;

    public GsmModem(PullerOptions options)
    {
        this.options = options;
        port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            NewLine = "\r\n",
            ReadTimeout = options.CommandTimeoutSeconds * 1000,
            WriteTimeout = options.CommandTimeoutSeconds * 1000,
            Encoding = Encoding.ASCII,
            DtrEnable = true,
            RtsEnable = true
        };
    }

    public static ModemConnection? FindConnection(PullerOptions options)
    {
        var availablePorts = SerialPort.GetPortNames();
        var candidates = string.IsNullOrWhiteSpace(options.PortName)
            ? availablePorts
            : new[] { options.PortName }.Concat(availablePorts.Where(port => !string.Equals(port, options.PortName, StringComparison.OrdinalIgnoreCase)));
        var baudRates = new[] { options.BaudRate, 115200, 57600, 38400, 19200, 9600 }.Distinct().ToArray();

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var baudRate in baudRates)
            {
                try
                {
                    using var modem = new GsmModem(options with { PortName = candidate, BaudRate = baudRate, CommandTimeoutSeconds = Math.Min(options.CommandTimeoutSeconds, 2) });
                    modem.Probe();
                    return new ModemConnection(candidate, baudRate);
                }
                catch (Exception exception)
                {
                    AppLog.Error("GSM modem probe failed.", exception, $"Port: {candidate}; Baud: {baudRate}");
                }
            }
        }
        return null;
    }

    public static string? FindPort(PullerOptions options) => FindConnection(options)?.PortName;

    public void Probe()
    {
        try
        {
            port.Open();
            Thread.Sleep(200);
            var response = Send("AT");
            if (!HasOk(response)) response = Send("AT");
            if (!HasOk(response))
            {
                throw new IOException($"No GSM modem response on {options.PortName}.");
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("GSM modem probe failed.", exception, $"Port: {options.PortName}");
            throw;
        }
    }

    public void Open()
    {
        try
        {
            port.Open();
            Thread.Sleep(200);
            var response = Send("AT");
            if (!HasOk(response)) response = Send("AT");
            if (!HasOk(response))
            {
                throw new IOException($"No GSM modem response on {options.PortName}.");
            }
            UnlockSim();
            Send("AT+CMGF=1");
            Send("AT+CPMS=\"SM\"");
        }
        catch (Exception exception)
        {
            AppLog.Error("GSM modem startup failed.", exception, $"Port: {options.PortName}");
            throw;
        }
    }

    private void UnlockSim()
    {
        var pinStatus = Send("AT+CPIN?", allowErrorResponse: true);
        ThrowForSimStatusError(pinStatus, pinWasSubmitted: false);
        if (IsSimReady(pinStatus))
        {
            return;
        }
        if (pinStatus.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase) || pinStatus.Contains("BLOCK", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"SIM is blocked on {options.PortName}. It requires a PUK.");
        }
        if (!pinStatus.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"SIM was not detected or is not ready on {options.PortName}.");
        }
        var pin = options.SimPin?.Trim();
        if (string.IsNullOrWhiteSpace(pin))
        {
            throw new IOException($"SIM PIN is required on {options.PortName}. Set txGSMSettings.PIN.");
        }
        if (!Regex.IsMatch(pin, "^\\d{4,8}$"))
        {
            throw new IOException($"SIM PIN format is invalid on {options.PortName}. It must contain 4 to 8 digits.");
        }

        var unlockResponse = Send($"AT+CPIN=\"{pin}\"", allowErrorResponse: true);
        ThrowForSimStatusError(unlockResponse, pinWasSubmitted: true);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(1000);
            var readyStatus = Send("AT+CPIN?", allowErrorResponse: true);
            ThrowForSimStatusError(readyStatus, pinWasSubmitted: true);
            if (IsSimReady(readyStatus))
            {
                return;
            }
            if (readyStatus.Contains("SIM PUK", StringComparison.OrdinalIgnoreCase) || readyStatus.Contains("BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"SIM is blocked on {options.PortName}. It requires a PUK.");
            }
        }
        throw new IOException($"SIM PIN was not accepted or SIM is still unlocking on {options.PortName}.");
    }

    private static bool IsSimReady(string response) => response.Contains("READY", StringComparison.OrdinalIgnoreCase);
    private static bool HasOk(string response) => Regex.IsMatch(response, @"(?:^|[\r\n])\s*OK\s*(?:[\r\n]|$)", RegexOptions.IgnoreCase);

    private void ThrowForSimStatusError(string response, bool pinWasSubmitted)
    {
        if (!response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) return;

        // Preserve the modem's precise response in the log, but never show it to the operator.
        AppLog.Error("GSM modem reported a SIM status error.", new IOException($"AT+CPIN response: {response.Trim()}"), $"Port: {options.PortName}");
        if (response.Contains("PUK", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(response, @"\+CME ERROR:\s*12\b", RegexOptions.IgnoreCase))
        {
            throw new IOException("SIM card is blocked. PUK required.");
        }
        if (pinWasSubmitted || response.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(response, @"\+CME ERROR:\s*(11|16)\b", RegexOptions.IgnoreCase))
        {
            throw new IOException("SIM PIN error. Check the configured PIN.");
        }
        throw new IOException("SIM card not detected. Insert or re-seat the SIM card.");
    }

    private static bool IsSimPresent(string cpinResponse, string ccidResponse) =>
        IsSimReady(cpinResponse) ||
        cpinResponse.Contains("SIM PIN", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(ccidResponse, @"\+CCID:\s*\d{10,}");

    public ModemDiagnostics ReadDiagnostics()
    {
        var simStatus = Send("AT+CPIN?", allowErrorResponse: true);
        var simResponse = string.Empty;
        try
        {
            simResponse = Send("AT+CCID");
        }
        catch (IOException)
        {
        }
        var simCard = IsSimPresent(simStatus, simResponse) ? "detected" : "not detected";
        var signalResponse = Send("AT+CSQ");
        var signalMatch = Regex.Match(signalResponse, @"\+CSQ:\s*(\d+)");
        var signal = signalMatch.Success ? $"{signalMatch.Groups[1].Value}/31" : "unknown";
        var networkResponse = Send("AT+COPS?");
        var networkMatch = Regex.Match(networkResponse, @"\+COPS:\s*\d+,\d+,""([^""]*)""");
        var network = networkMatch.Success ? NetworkNames.Resolve(networkMatch.Groups[1].Value) : "network unknown";
        return new ModemDiagnostics(simCard, signal, network);
    }

    public IReadOnlyList<SmsMessage> ReadUnreadMessages()
    {
        return ParseUnreadMessages(Send("AT+CMGL=\"REC UNREAD\""), options.MessageProfile);
    }

    public static IReadOnlyList<SmsMessage> ParseUnreadMessages(string response, string messageProfile = "Auto Detect")
    {
        var messages = new List<SmsMessage>();
        var lines = response.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var indexMatch = Regex.Match(lines[lineIndex], @"\+CMGL:\s*(\d+)", RegexOptions.IgnoreCase);
            var senderMatch = Regex.Match(lines[lineIndex], @"\+CMGL:\s*\d+,\s*""[^""]*"",\s*""([^""]*)""", RegexOptions.IgnoreCase);
            var dateMatch = Regex.Match(lines[lineIndex], @"(\d{2}/\d{2}/\d{2}),(\d{2}:\d{2}:\d{2})", RegexOptions.IgnoreCase);
            if (!indexMatch.Success || !senderMatch.Success || !dateMatch.Success)
            {
                continue;
            }

            var bodyLines = new List<string>();
            while (lineIndex + 1 < lines.Length && !lines[lineIndex + 1].Contains("+CMGL:", StringComparison.OrdinalIgnoreCase) && !string.Equals(lines[lineIndex + 1].Trim(), "OK", StringComparison.OrdinalIgnoreCase))
            {
                bodyLines.Add(lines[++lineIndex].Trim());
            }
            var body = string.Join(" ", bodyLines.Where(line => line.Length > 0));
            messages.Add(SmsParser.Parse(
                int.Parse(indexMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                senderMatch.Groups[1].Value,
                dateMatch.Groups[1].Value,
                dateMatch.Groups[2].Value,
                body,
                messageProfile));
        }
        return messages;
    }

    public void DeleteMessage(int index) => Send($"AT+CMGD={index}");

    public void Dispose() => port.Dispose();

    private string Send(string command, bool allowErrorResponse = false)
    {
        port.DiscardInBuffer();
        port.Write(command + "\r");
        var result = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(options.CommandTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            result.Append(port.ReadExisting());
            if (result.ToString().Contains("\r\nOK", StringComparison.OrdinalIgnoreCase) || result.ToString().Contains("\r\nERROR", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            Thread.Sleep(50);
        }

        var response = result.ToString();
        if (!allowErrorResponse && response.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            // Do not write command values here: AT+CPIN contains the SIM PIN.
            throw new IOException($"Modem rejected command {CommandName(command)}: {response.Trim()}");
        }
        return response;
    }

    private static string CommandName(string command) => command.StartsWith("AT+CPIN", StringComparison.OrdinalIgnoreCase) ? "AT+CPIN" : command;
}
