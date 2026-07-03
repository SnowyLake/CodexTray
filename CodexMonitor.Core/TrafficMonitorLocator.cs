namespace CodexMonitor.Core;

public static class TrafficMonitorLocator
{
    /// <summary>
    /// Returns true when a directory looks like a TrafficMonitor installation.
    /// </summary>
    public static bool IsTrafficMonitorDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, "TrafficMonitor.exe"));
    }

    /// <summary>
    /// Attempts to find a TrafficMonitor installation directory.
    /// </summary>
    public static string AutoDetect(string? savedDirectory = null)
    {
        foreach (string candidate in EnumerateCandidates(savedDirectory))
        {
            if (IsTrafficMonitorDirectory(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Enumerates likely TrafficMonitor installation directories.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidates(string? savedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(savedDirectory))
        {
            yield return savedDirectory;
        }

        foreach (string directory in EnumerateSearchRoots())
        {
            foreach (string match in FindTrafficMonitorExe(directory))
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
    /// Finds TrafficMonitor.exe below a root directory.
    /// </summary>
    private static IEnumerable<string> FindTrafficMonitorExe(string root)
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
            files = Directory.EnumerateFiles(root, "TrafficMonitor.exe", options).Take(3).GetEnumerator();
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
