namespace CodexTray.App;

internal static class ApplicationConfiguration
{
    /// <summary>
    /// Initializes global WinForms application settings.
    /// </summary>
    public static void Initialize()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
    }
}
