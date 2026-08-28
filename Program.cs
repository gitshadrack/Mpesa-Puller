using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile(ConfigPaths.UserSettingsFile, optional: true, reloadOnChange: true)
    .Build();
using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var database = new DatabaseWriter(configuration, loggerFactory.CreateLogger<DatabaseWriter>());
Application.Run(new MainForm(configuration, database));
