using Microsoft.Win32;
using System.Runtime.Versioning;

namespace CodexTray.Core;

[SupportedOSPlatform("windows")]
public static class StartupManager
{
    private const string k_RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Returns true when the current exe is registered to start with Windows.
    /// </summary>
    public static bool IsEnabled(string executablePath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(k_RunKeyPath, false);
        string? value = key?.GetValue(CodexTrayDefaults.StartupRunValueName) as string;
        return string.Equals(NormalizeRunValue(value), Quote(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enables or disables the current exe in the HKCU Run key.
    /// </summary>
    public static void SetEnabled(string executablePath, bool enabled)
    {
        if (enabled && string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Unable to resolve the executable path for startup registration.");
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(k_RunKeyPath, true) ?? throw new InvalidOperationException("Unable to open HKCU Run key.");
        if (enabled)
        {
            key.SetValue(CodexTrayDefaults.StartupRunValueName, Quote(executablePath));
            return;
        }

        key.DeleteValue(CodexTrayDefaults.StartupRunValueName, false);
    }

    /// <summary>
    /// Quotes an executable path for the Run key.
    /// </summary>
    private static string Quote(string executablePath)
    {
        return $"\"{executablePath}\"";
    }

    /// <summary>
    /// Normalizes a persisted Run value for comparison.
    /// </summary>
    private static string NormalizeRunValue(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
