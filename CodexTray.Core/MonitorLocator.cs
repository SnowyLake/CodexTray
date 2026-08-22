using System.IO.Enumeration;

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
    public static string AutoDetect(string executableName, string? savedDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string candidate in EnumerateCandidates(executableName, savedDirectory, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    private static IEnumerable<string> EnumerateCandidates(string executableName, string? savedDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(savedDirectory))
        {
            yield return savedDirectory;
        }

        foreach (string directory in EnumeratePreferredSearchRoots(cancellationToken).Concat(EnumerateSearchRoots(cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string match in FindExecutable(directory, executableName, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Path.GetDirectoryName(match) ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Enumerates common install locations before walking drive roots.
    /// </summary>
    private static IEnumerable<string> EnumeratePreferredSearchRoots(CancellationToken cancellationToken)
    {
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder) && Directory.Exists(folder))
            {
                yield return folder;
            }
        }
    }

    /// <summary>
    /// Enumerates local drive roots that can be searched.
    /// </summary>
    private static IEnumerable<string> EnumerateSearchRoots(CancellationToken cancellationToken)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    private static IEnumerable<string> FindExecutable(string root, string executableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerator<string> files;
        try
        {
            FileSystemEnumerable<string> enumerable = new(root, (ref FileSystemEntry entry) => entry.ToFullPath(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return !entry.IsDirectory && entry.FileName.Equals(executableName, StringComparison.OrdinalIgnoreCase);
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return !IsSkippedSearchDirectory(entry.FileName);
                },
            };
            files = enumerable.Take(3).GetEnumerator();
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
                cancellationToken.ThrowIfCancellationRequested();
                string file;
                try
                {
                    if (!files.MoveNext())
                    {
                        yield break;
                    }

                    file = files.Current;
                    cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Returns true when a directory should not be searched for monitor executables.
    /// </summary>
    private static bool IsSkippedSearchDirectory(ReadOnlySpan<char> name)
    {
        return name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("WinSxS", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }
}
