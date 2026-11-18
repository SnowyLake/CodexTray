using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

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

    public static bool CanReplaceAllText(string path, string expectedContents)
    {
        return TryReplaceAllText(path, expectedContents, contents: null);
    }

    public static bool TryReplaceAllText(string path, string expectedContents, string? contents)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // A transacted writer isolates the comparison and update from other file writers.
            using SafeTransactionHandle transaction = CreateTransaction(nint.Zero, nint.Zero, 0, 0, 0, 0, nint.Zero);
            if (transaction.IsInvalid)
            {
                return false;
            }

            bool committed = false;
            try
            {
                using (SafeFileHandle file = CreateFileTransacted(path, 0xC0000000, (uint)FileShare.Read, nint.Zero, 3, 0x80, nint.Zero, transaction, nint.Zero, nint.Zero))
                {
                    if (file.IsInvalid)
                    {
                        return false;
                    }

                    using FileStream stream = new(file, FileAccess.ReadWrite);
                    using (StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
                    {
                        if (!string.Equals(reader.ReadToEnd(), expectedContents, StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }

                    if (contents != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(contents);
                        stream.Position = 0;
                        stream.Write(bytes);
                        stream.SetLength(bytes.Length);
                        stream.Flush();
                    }
                }

                if (contents == null)
                {
                    return true;
                }

                committed = CommitTransaction(transaction);
                return committed;
            }
            finally
            {
                if (!committed)
                {
                    RollbackTransaction(transaction);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("KtmW32.dll", SetLastError = true)]
    private static extern SafeTransactionHandle CreateTransaction(nint attributes, nint unitOfWork, uint options, uint isolationLevel, uint isolationFlags, uint timeout, nint description);

    [DllImport("KtmW32.dll", SetLastError = true)]
    private static extern bool CommitTransaction(SafeTransactionHandle transaction);

    [DllImport("KtmW32.dll", SetLastError = true)]
    private static extern bool RollbackTransaction(SafeTransactionHandle transaction);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileTransactedW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileTransacted(string path, uint access, uint share, nint attributes, uint creation, uint flags, nint template,
                                                              SafeTransactionHandle transaction, nint miniVersion, nint extendedParameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private sealed class SafeTransactionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTransactionHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
