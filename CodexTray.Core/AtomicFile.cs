namespace CodexTray.Core;

internal static class AtomicFile
{
    /// <summary>
    /// Replaces a text file after writing a temporary sibling file.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (UnauthorizedAccessException)
                {
                    File.Move(tempPath, path, true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
