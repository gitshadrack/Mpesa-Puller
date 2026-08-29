using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, eventArgs) => AppLog.Error("Unhandled Windows Forms error.", eventArgs.Exception);
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception exception) AppLog.Error("Unhandled application error.", exception);
    else AppLog.Info($"Unhandled non-exception application error: {eventArgs.ExceptionObject}");
};
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    AppLog.Error("Unobserved task error.", eventArgs.Exception);
    eventArgs.SetObserved();
};

try
{
    AppLog.Info("M-Pesa Message Puller starting.");
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile(ConfigPaths.UserSettingsFile, optional: true, reloadOnChange: true)
        .Build();
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var database = new DatabaseWriter(configuration, loggerFactory.CreateLogger<DatabaseWriter>());
    Application.Run(new MainForm(configuration, database));
    AppLog.Info("M-Pesa Message Puller stopped.");
}
catch (Exception exception)
{
    AppLog.Error("Application startup failed.", exception);
    MessageBox.Show($"The application could not start. See the log file:\n{AppLog.CurrentLogFile}", "M-Pesa Puller", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
