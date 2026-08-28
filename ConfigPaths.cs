static class ConfigPaths
{
    public static string UserSettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPesa");
    public static string UserSettingsFile => Path.Combine(UserSettingsDirectory, "appsettings.json");
}
