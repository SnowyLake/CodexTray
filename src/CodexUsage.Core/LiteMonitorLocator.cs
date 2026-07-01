namespace CodexUsage.Core;

public static class LiteMonitorLocator
{
    private static readonly string[] s_FixedDirectories =
    [
        @"D:\Tools\LiteMonitor_v1.3.6-win-x64",
    ];

    /// <summary>
    /// Returns true when a directory looks like a LiteMonitor installation.
    /// </summary>
    public static bool IsLiteMonitorDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, "LiteMonitor.exe"));
    }

    /// <summary>
    /// Attempts to find a LiteMonitor installation directory.
    /// </summary>
    public static string AutoDetect(string? savedDirectory = null)
    {
        foreach (string candidate in EnumerateCandidates(savedDirectory))
        {
            if (IsLiteMonitorDirectory(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Enumerates likely LiteMonitor installation directories.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidates(string? savedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(savedDirectory))
        {
            yield return savedDirectory;
        }

        foreach (string directory in s_FixedDirectories)
        {
            yield return directory;
        }

        foreach (string directory in EnumerateToolDirectories())
        {
            yield return directory;
        }

        foreach (string directory in EnumerateProgramRoots())
        {
            foreach (string match in FindLiteMonitorExe(directory))
            {
                yield return Path.GetDirectoryName(match) ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Enumerates LiteMonitor-like directories below D:\Tools.
    /// </summary>
    private static IEnumerable<string> EnumerateToolDirectories()
    {
        const string toolsRoot = @"D:\Tools";
        if (!Directory.Exists(toolsRoot))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(toolsRoot, "LiteMonitor*"))
        {
            yield return directory;
        }
    }

    /// <summary>
    /// Enumerates roots that may contain application installs.
    /// </summary>
    private static IEnumerable<string> EnumerateProgramRoots()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        foreach (string root in roots.Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root)))
        {
            yield return root;
        }
    }

    /// <summary>
    /// Finds LiteMonitor.exe below a root directory.
    /// </summary>
    private static IEnumerable<string> FindLiteMonitorExe(string root)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (string file in Directory.EnumerateFiles(root, "LiteMonitor.exe", options).Take(3))
        {
            yield return file;
        }
    }
}
