using System.Text;

static class AppLog
{
    private static readonly object Sync = new();

    public static string CurrentLogFile => Path.Combine(ConfigPaths.LogsDirectory, $"mpesa-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    public static void Error(string message, Exception exception, string? context) =>
        Write("ERROR", string.IsNullOrWhiteSpace(context) ? message : $"{message}{Environment.NewLine}Context: {context}", exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.LogsDirectory);
            var entry = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .Append(level).Append(' ').AppendLine(message);
            // Exception.ToString() includes the exception type, message, stack trace, and every inner exception.
            if (exception is not null) entry.AppendLine(exception.ToString());
            lock (Sync) File.AppendAllText(CurrentLogFile, entry.AppendLine().ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never prevent the puller from running.
        }
    }
}
