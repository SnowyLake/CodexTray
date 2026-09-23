namespace CodexTray.Core;

/// <summary>
/// Replaces an installation directory from an extracted release and restores the backup on failure.
/// </summary>
public static class AppUpdateInstaller
{
    /// <summary>
    /// Backs up <paramref name="targetDirectory"/>, then copies the release over it.
    /// The root <c>settings.json</c> already in the target is left in place.
    /// </summary>
    public static void Apply(string sourceDirectory, string targetDirectory, string backupDirectory)
    {
        string source = Path.GetFullPath(sourceDirectory);
        string target = Path.GetFullPath(targetDirectory);
        string backup = Path.GetFullPath(backupDirectory);
        EnsureDisjoint(source, target, backup);
        try
        {
            AppUpdatePackage.ValidateLayout(source);
        }
        catch (AppUpdateException exception)
        {
            throw new AppUpdateException(exception.Message, canRestartPreviousVersion: true, exception);
        }

        string targetExecutable = Path.Combine(target, CodexTrayDefaults.AppName + ".exe");
        if (!File.Exists(targetExecutable))
        {
            throw new AppUpdateException("The update target is not a CodexTray installation.", canRestartPreviousVersion: true);
        }

        AppUpdateWork.EnsurePublishedInstall(targetExecutable);

        if (Directory.Exists(backup) || File.Exists(backup))
        {
            throw new AppUpdateException("The update backup could not be created, so the installation was not changed.", canRestartPreviousVersion: true);
        }

        try
        {
            CopyTree(target, backup);
        }
        catch (Exception exception)
        {
            AppUpdateWork.DeleteQuietly(backup);
            throw new AppUpdateException(
                "CodexTray could not back up the current installation, so it was not changed.",
                canRestartPreviousVersion: true,
                exception);
        }

        try
        {
            ReplaceInstallation(source, target);
        }
        catch (Exception exception)
        {
            try
            {
                Restore(backup, target);
            }
            catch (Exception restoreException)
            {
                throw new AppUpdateException(
                    "The update failed and the previous version could not be restored. Install the release zip manually.",
                    canRestartPreviousVersion: false,
                    restoreException);
            }

            throw new AppUpdateException(
                "The update could not replace the installed files. CodexTray was restored to the previous version.",
                canRestartPreviousVersion: true,
                exception);
        }
    }

    /// <summary>
    /// Copies a backup back over the installation and removes files the backup does not contain.
    /// </summary>
    public static void Restore(string backupDirectory, string targetDirectory)
    {
        string backup = Path.GetFullPath(backupDirectory);
        string target = Path.GetFullPath(targetDirectory);
        List<string> backupFiles = EnumerateRelativeFiles(backup);
        HashSet<string> backupSet = new(backupFiles, StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in backupFiles)
        {
            CopyFile(Path.Combine(backup, relativePath), Path.Combine(target, relativePath));
        }

        foreach (string relativePath in EnumerateRelativeFiles(target))
        {
            if (!backupSet.Contains(relativePath))
            {
                DeleteFile(Path.Combine(target, relativePath));
            }
        }
    }

    /// <summary>
    /// Rejects source, target, and backup paths that contain one another.
    /// </summary>
    private static void EnsureDisjoint(string source, string target, string backup)
    {
        if (IsSameOrChild(source, target) || IsSameOrChild(target, source)
            || IsSameOrChild(backup, source) || IsSameOrChild(source, backup)
            || IsSameOrChild(backup, target) || IsSameOrChild(target, backup))
        {
            throw new AppUpdateException("The update folders overlap, so the installation was not changed.", canRestartPreviousVersion: true);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is the same as or inside <paramref name="parent"/>.
    /// </summary>
    private static bool IsSameOrChild(string path, string parent)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies every release file except the preserved settings file, and copies the executable last.
    /// </summary>
    private static void ReplaceInstallation(string source, string target)
    {
        List<string> sourceFiles = EnumerateRelativeFiles(source);
        foreach (string relativePath in sourceFiles)
        {
            if (IsPreservedSettings(relativePath) || IsExecutable(relativePath))
            {
                continue;
            }

            CopyFile(Path.Combine(source, relativePath), Path.Combine(target, relativePath));
        }

        string executable = CodexTrayDefaults.AppName + ".exe";
        if (!sourceFiles.Contains(executable, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppUpdateException("The update package is missing CodexTray.exe.");
        }

        CopyFile(Path.Combine(source, executable), Path.Combine(target, executable));
    }

    /// <summary>
    /// Returns true for the root settings file that belongs to the user.
    /// </summary>
    private static bool IsPreservedSettings(string relativePath)
    {
        return string.Equals(relativePath, CodexTrayDefaults.SettingsFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for the application executable at the root of a package.
    /// </summary>
    private static bool IsExecutable(string relativePath)
    {
        return string.Equals(relativePath, CodexTrayDefaults.AppName + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies every file under <paramref name="source"/> to <paramref name="target"/>.
    /// </summary>
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string relativePath in EnumerateRelativeFiles(source))
        {
            CopyFile(Path.Combine(source, relativePath), Path.Combine(target, relativePath));
        }
    }

    /// <summary>
    /// Lists files under a directory. Relative paths use the local directory separator.
    /// </summary>
    private static List<string> EnumerateRelativeFiles(string root)
    {
        List<string> files = [];
        EnumerateRelativeFiles(root, string.Empty, files);
        return files;
    }

    /// <summary>
    /// Walks a directory and skips reparse points.
    /// </summary>
    private static void EnumerateRelativeFiles(string root, string relativeDirectory, List<string> files)
    {
        string directory = relativeDirectory.Length == 0 ? root : Path.Combine(root, relativeDirectory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            string relativePath = relativeDirectory.Length == 0
                ? Path.GetFileName(path)
                : Path.Combine(relativeDirectory, Path.GetFileName(path));
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                EnumerateRelativeFiles(root, relativePath, files);
                continue;
            }

            files.Add(relativePath);
        }
    }

    /// <summary>
    /// Copies one file and retries while the destination is still locked.
    /// </summary>
    private static void CopyFile(string sourcePath, string destinationPath)
    {
        string? parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Retry(destinationPath, () =>
        {
            if (File.Exists(destinationPath))
            {
                File.SetAttributes(destinationPath, FileAttributes.Normal);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        });
    }

    /// <summary>
    /// Deletes one file and retries while it is still locked.
    /// </summary>
    private static void DeleteFile(string path)
    {
        Retry(path, () =>
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        });
    }

    /// <summary>
    /// Repeats a file operation while the target is still locked.
    /// </summary>
    private static void Retry(string path, Action action)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= CodexTrayDefaults.UpdateFileCopyAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                if (attempt == CodexTrayDefaults.UpdateFileCopyAttempts)
                {
                    break;
                }

                Thread.Sleep(CodexTrayDefaults.UpdateFileCopyRetryDelayMilliseconds);
            }
        }

        throw new IOException($"Could not update {Path.GetFileName(path)}.", lastError);
    }
}
