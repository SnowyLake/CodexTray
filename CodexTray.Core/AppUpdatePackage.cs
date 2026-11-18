using System.IO.Compression;
using System.Security.Cryptography;

namespace CodexTray.Core;

/// <summary>
/// Checks, extracts, and stages a downloaded release package.
/// </summary>
public static class AppUpdatePackage
{
    /// <summary>
    /// Computes the lowercase SHA-256 hex of a file.
    /// </summary>
    public static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Rejects a package whose size or SHA-256 differs from the release metadata.
    /// </summary>
    public static void Verify(string zipPath, AppUpdateRelease release)
    {
        FileInfo info = new(zipPath);
        if (info.Length != release.PackageSize)
        {
            throw new AppUpdateException("The update package size does not match the release.");
        }

        if (!string.Equals(ComputeSha256(zipPath), release.PackageSha256, StringComparison.Ordinal))
        {
            throw new AppUpdateException("The update package hash does not match the release.");
        }
    }

    /// <summary>
    /// Extracts a package and rejects entries that would write outside the destination.
    /// </summary>
    public static void Extract(string zipPath, string destinationDirectory)
    {
        string destination = Path.GetFullPath(destinationDirectory);
        AppUpdatePaths.EnsureSafePath(zipPath);
        AppUpdatePaths.EnsureSafePath(destination);
        Directory.CreateDirectory(destination);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = GetSafeEntryPath(destination, entry.FullName);
            AppUpdatePaths.EnsureSafePath(target);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Requires the executable, resources, and plugin files shipped in a release zip.
    /// </summary>
    public static void ValidateLayout(string directory)
    {
        foreach (string relativePath in CodexTrayDefaults.ReleasePackageRelativePaths)
        {
            string path = Path.Combine(directory, relativePath);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new AppUpdateException("The update package is missing required files.");
            }
        }
    }

    /// <summary>
    /// Maps a zip entry onto the destination directory or rejects an unsafe path.
    /// </summary>
    private static string GetSafeEntryPath(string destination, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.Contains(':') || entryName.Contains('\0'))
        {
            throw new AppUpdateException("The update package contains an unsafe path.");
        }

        string normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new AppUpdateException("The update package contains an unsafe path.");
        }

        string combined = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string root = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppUpdateException("The update package contains an unsafe path.");
        }

        return combined;
    }
}

/// <summary>
/// Owns temporary update directories and the executable handoff.
/// </summary>
public static class AppUpdateWork
{
    /// <summary>
    /// Creates a new working directory for one update attempt.
    /// </summary>
    public static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), CodexTrayDefaults.AppName, "update", Guid.NewGuid().ToString("N"));
        AppUpdatePaths.EnsureSafePath(path);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Deletes update working directories that are older than the retention window.
    /// </summary>
    public static void CleanupStaleDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), CodexTrayDefaults.AppName, "update");
        try
        {
            AppUpdatePaths.EnsureSafePath(root);
            if (!Directory.Exists(root))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddHours(-CodexTrayDefaults.UpdateWorkRetentionHours);
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    AppUpdatePaths.EnsureSafePath(directory);
                    if (Directory.GetCreationTimeUtc(directory) > cutoff)
                    {
                        continue;
                    }

                    DeleteQuietly(directory);
                }
                catch (Exception exception) when (exception is AppUpdateException or IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is AppUpdateException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes a directory and ignores a file that is still in use.
    /// </summary>
    public static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            AppUpdatePaths.EnsureSafePath(path);
            if (!Directory.Exists(path))
            {
                return;
            }

            AppUpdatePaths.EnumerateRelativeFiles(path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is AppUpdateException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Copies the new executable out of the extracted package so it can replace the running install.
    /// </summary>
    public static AppUpdateHandoff PrepareHandoff(string extractedDirectory, string workDirectory, string currentExecutablePath)
    {
        AppUpdatePackage.ValidateLayout(extractedDirectory);
        string currentExecutable = Path.GetFullPath(currentExecutablePath);
        if (!string.Equals(Path.GetFileName(currentExecutable), CodexTrayDefaults.AppName + ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(currentExecutable))
        {
            throw new AppUpdateException("Updates can only be applied to CodexTray.exe.");
        }

        EnsurePublishedInstall(currentExecutable);

        string? targetDirectory = Path.GetDirectoryName(currentExecutable);
        if (string.IsNullOrEmpty(targetDirectory))
        {
            throw new AppUpdateException("Updates can only be applied to CodexTray.exe.");
        }

        AppUpdatePaths.EnumerateRelativeFiles(extractedDirectory);
        AppUpdatePaths.EnumerateRelativeFiles(targetDirectory);
        EnsureWritable(targetDirectory);
        string applyDirectory = Path.Combine(workDirectory, "apply");
        AppUpdatePaths.EnsureSafePath(applyDirectory);
        Directory.CreateDirectory(applyDirectory);
        string applyExecutable = Path.Combine(applyDirectory, CodexTrayDefaults.AppName + ".exe");
        AppUpdatePaths.EnsureSafePath(applyExecutable);
        File.Copy(Path.Combine(extractedDirectory, CodexTrayDefaults.AppName + ".exe"), applyExecutable, overwrite: true);
        return new AppUpdateHandoff(applyExecutable, Path.GetFullPath(extractedDirectory), targetDirectory, currentExecutable);
    }

    /// <summary>
    /// Returns true when the executable is a published single-file install.
    /// A root CodexTray.dll marks a normal build output. The TrafficMonitor plugin copy does not.
    /// </summary>
    public static bool IsPublishedInstall(string executablePath)
    {
        string executable = Path.GetFullPath(executablePath);
        string? directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory)
            || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), CodexTrayDefaults.AppName + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !File.Exists(Path.Combine(directory, CodexTrayDefaults.AppName + ".dll"));
    }

    /// <summary>
    /// Rejects a development build before an update check or file replacement.
    /// </summary>
    public static void EnsurePublishedInstall(string executablePath)
    {
        if (!IsPublishedInstall(executablePath))
        {
            throw new AppUpdateException("Updates are unavailable from a development build.", canRestartPreviousVersion: true);
        }
    }

    /// <summary>
    /// Rejects an executable path that crosses a reparse point.
    /// </summary>
    public static void EnsureSafeExecutablePath(string executablePath)
    {
        AppUpdatePaths.EnsureSafePath(executablePath);
    }

    /// <summary>
    /// Checks that the installation directory can be modified before the app exits.
    /// </summary>
    private static void EnsureWritable(string targetDirectory)
    {
        string probe = Path.Combine(targetDirectory, $".codextray-update-{Guid.NewGuid():N}.probe");
        try
        {
            AppUpdatePaths.EnsureSafePath(probe);
            File.WriteAllText(probe, "ok");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppUpdateException("CodexTray cannot update this folder. Move it to a writable location and try again.", innerException: exception);
        }
        finally
        {
            try
            {
                AppUpdatePaths.EnsureSafePath(probe);
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception exception) when (exception is AppUpdateException or IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
