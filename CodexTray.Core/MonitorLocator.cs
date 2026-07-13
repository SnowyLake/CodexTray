namespace CodexTray.Core;

internal static class MonitorLocator
{
    /// <summary>
    /// Returns true when a directory contains the target monitor executable.
    /// </summary>
    public static bool IsMonitorDirectory(string? directory, string executableName)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, executableName));
    }

    /// <summary>
    /// Attempts to find a monitor installation directory.
    /// </summary>
    public static string AutoDetect(string executableName, string? savedDirectory = null)
    {
        foreach (string candidate in EnumerateCandidates(executableName, savedDirectory))
        {
            if (IsMonitorDirectory(candidate, executableName))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Enumerates likely monitor installation directories.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidates(string executableName, string? savedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(savedDirectory))
        {
            yield return savedDirectory;
        }

        foreach (string directory in EnumerateSearchRoots())
        {
            foreach (string match in FindExecutable(directory, executableName))
            {
                yield return Path.GetDirectoryName(match) ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Enumerates local drive roots that can be searched.
    /// </summary>
    private static IEnumerable<string> EnumerateSearchRoots()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            bool canSearch;
            try
            {
                canSearch = (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable) && drive.IsReady;
            }
            catch (IOException)
            {
                canSearch = false;
            }
            catch (UnauthorizedAccessException)
            {
                canSearch = false;
            }

            if (canSearch)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    /// <summary>
    /// Finds a target executable below a root directory.
    /// </summary>
    private static IEnumerable<string> FindExecutable(string root, string executableName)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        IEnumerator<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, executableName, options).Take(3).GetEnumerator();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (files)
        {
            while (true)
            {
                string file;
                try
                {
                    if (!files.MoveNext())
                    {
                        yield break;
                    }

                    file = files.Current;
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return file;
            }
        }
    }
}
