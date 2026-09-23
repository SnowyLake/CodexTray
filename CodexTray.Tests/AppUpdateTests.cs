using CodexTray.Core;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CodexTray.Tests;

internal static class AppUpdateTests
{
    /// <summary>
    /// Runs the application update checks.
    /// </summary>
    public static async Task<int> RunAllAsync()
    {
        int failures = 0;
        failures += await RunAsync("parses the latest GitHub release asset", TestParseLatestReleaseAsync);
        failures += await RunAsync("compares the running version with a release", TestCompareVersionAsync);
        failures += await RunAsync("verifies the release package hash and size", TestVerifyPackageAsync);
        failures += await RunAsync("rejects update packages that escape the destination", TestRejectZipSlipAsync);
        failures += await RunAsync("prepares an update handoff from a release package", TestPrepareHandoffAsync);
        failures += await RunAsync("applies an update without replacing settings", TestApplyUpdateAsync);
        failures += await RunAsync("restores the previous installation when replacement fails", TestRestoreFailedUpdateAsync);
        failures += await RunAsync("round-trips update process arguments", TestUpdateArgumentsAsync);
        failures += await RunAsync("rejects updates from a development build", TestRejectDevelopmentBuildAsync);
        return failures;
    }

    /// <summary>
    /// Runs one update test and records its failure.
    /// </summary>
    private static async Task<int> RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL {name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Parses a stable release asset and rejects tags, URLs, and digests that cannot be installed.
    /// </summary>
    private static Task TestParseLatestReleaseAsync()
    {
        string packageName = CodexTrayDefaults.BuildReleasePackageName(new Version(5, 2, 0));
        string downloadUrl = $"https://github.com/SnowyLake/CodexTray/releases/download/v5.2.0/{packageName}";
        AppUpdateRelease release = AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0", packageName, downloadUrl, 128, Sha("abc")));
        AssertEqual(new Version(5, 2, 0), release.Version, "release version");
        AssertEqual(packageName, release.PackageName, "package name");
        AssertEqual(downloadUrl, release.PackageUrl.AbsoluteUri, "package url");
        AssertEqual(Sha("abc"), release.PackageSha256, "normalized digest");

        AssertThrows(() => AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0-beta", packageName, downloadUrl, 128, Sha("abc"))), "stable X.Y.Z");
        AssertThrows(() => AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0", "CodexTray-src.zip", downloadUrl, 128, Sha("abc"))), packageName);
        AssertThrows(() => AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0", packageName, downloadUrl, 128, null)), "SHA-256");
        AssertThrows(() => AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0", packageName, "http://github.com/SnowyLake/CodexTray/releases/download/v5.2.0/" + packageName, 128, Sha("abc"))), "GitHub release asset");
        AssertThrows(() => AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0", packageName, "https://example.com/" + packageName, 128, Sha("abc"))), "GitHub release asset");
        AssertThrows(() => AppUpdateReleaseParser.ParseLatest(ReleaseJson("v5.2.0", packageName, downloadUrl, CodexTrayDefaults.MaximumReleasePackageBytes + 1, Sha("abc"))), "size");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Compares stable versions and reports a GitHub rate limit.
    /// </summary>
    private static async Task TestCompareVersionAsync()
    {
        AssertTrue(AppUpdateVersion.TryParseTag("v5.1.1", out Version? tag) && tag == new Version(5, 1, 1), "tag parse");
        AssertTrue(!AppUpdateVersion.TryParseTag("5.1.1", out _), "tag requires v");
        AssertTrue(!AppUpdateVersion.TryParseTag("v5.1.1-beta", out _), "tag rejects prerelease");
        AssertTrue(AppUpdateVersion.TryParseAssemblyVersion("5.1.1", out Version? assembly) && assembly == new Version(5, 1, 1), "assembly parse");
        AssertTrue(!AppUpdateVersion.TryParseAssemblyVersion("5.1.1+commit", out _), "assembly rejects metadata");
        AssertEqual(AppUpdateAvailability.UpdateAvailable, AppUpdateVersion.Compare(new Version(5, 1, 1), new Version(5, 2, 0)), "newer release");
        AssertEqual(AppUpdateAvailability.UpToDate, AppUpdateVersion.Compare(new Version(5, 2, 0), new Version(5, 2, 0)), "same release");
        AssertEqual(AppUpdateAvailability.UpToDate, AppUpdateVersion.Compare(new Version(5, 3, 0), new Version(5, 2, 0)), "local is newer");

        using HttpClient client = new(new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("API rate limit exceeded"),
        }));
        AppUpdateSession session = new(new GitHubAppUpdateClient(client));
        await AssertThrowsAsync(() => session.CheckAsync(new Version(5, 1, 1), CancellationToken.None), "rate limit");
    }

    /// <summary>
    /// Rejects a package whose bytes do not match the published size or digest.
    /// </summary>
    private static Task TestVerifyPackageAsync()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "package.zip");
        byte[] body = Encoding.UTF8.GetBytes("zip-body");
        File.WriteAllBytes(path, body);
        string sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        AppUpdateRelease release = CreateRelease(new Version(1, 2, 3), body.Length, sha);
        AppUpdatePackage.Verify(path, release);

        AssertThrows(() => AppUpdatePackage.Verify(path, CreateRelease(new Version(1, 2, 3), body.Length, Sha("fff"))), "hash");
        AssertThrows(() => AppUpdatePackage.Verify(path, CreateRelease(new Version(1, 2, 3), body.Length + 1, sha)), "size");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rejects zip entries that would write outside the extraction directory.
    /// </summary>
    private static Task TestRejectZipSlipAsync()
    {
        using TempDirectory temp = new();
        string zipPath = Path.Combine(temp.Path, "slip.zip");
        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../evil.txt", "bad");
            WriteEntry(archive, "Resources/../../outside.txt", "bad");
        }

        string destination = Path.Combine(temp.Path, "out");
        AssertThrows(() => AppUpdatePackage.Extract(zipPath, destination), "unsafe path");
        AssertTrue(!File.Exists(Path.Combine(temp.Path, "evil.txt")), "parent escape");
        AssertTrue(!File.Exists(Path.Combine(temp.Path, "outside.txt")), "nested escape");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Downloads a verified package and stages the new executable outside the installation.
    /// </summary>
    private static async Task TestPrepareHandoffAsync()
    {
        using TempDirectory install = new();
        WritePackage(install.Path, "old");
        File.WriteAllText(Path.Combine(install.Path, CodexTrayDefaults.SettingsFileName), "user-settings");
        string packageName = CodexTrayDefaults.BuildReleasePackageName(new Version(9, 9, 9));
        string downloadUrl = $"https://github.com/SnowyLake/CodexTray/releases/download/v9.9.9/{packageName}";
        byte[] packageBytes = CreatePackageZip("new");
        string sha = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        string json = ReleaseJson("v9.9.9", packageName, downloadUrl, packageBytes.Length, sha);
        using HttpClient client = new(new StaticHttpHandler(request =>
        {
            AssertTrue(request.Headers.UserAgent.ToString().Contains("CodexTray", StringComparison.Ordinal), "user agent");
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri == CodexTrayDefaults.GitHubLatestReleaseUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            if (uri == downloadUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes),
                };
            }

            throw new InvalidOperationException($"unexpected request URI: {uri}");
        }));
        AppUpdateSession session = new(new GitHubAppUpdateClient(client));
        AppUpdateCheckResult available = await session.CheckAsync(new Version(9, 9, 8), CancellationToken.None);
        AssertEqual(AppUpdateAvailability.UpdateAvailable, available.Availability, "update available");
        AppUpdateHandoff handoff = await session.PrepareAsync(available.LatestRelease, Path.Combine(install.Path, CodexTrayDefaults.AppName + ".exe"), CancellationToken.None);
        try
        {
            AssertTrue(File.Exists(handoff.ApplyExecutablePath), "staged executable");
            AssertTrue(!string.Equals(Path.GetFullPath(handoff.ApplyExecutablePath), Path.Combine(install.Path, CodexTrayDefaults.AppName + ".exe"), StringComparison.OrdinalIgnoreCase), "staged outside install");
            AssertEqual("user-settings", File.ReadAllText(Path.Combine(install.Path, CodexTrayDefaults.SettingsFileName)), "settings untouched");
            AssertTrue(File.ReadAllText(Path.Combine(handoff.SourceDirectory, CodexTrayDefaults.AppName + ".exe")).StartsWith("new:", StringComparison.Ordinal), "extracted executable");
            AssertThrows(
                () => AppUpdateWork.PrepareHandoff(handoff.SourceDirectory, Path.Combine(install.Path, "work"), Path.Combine(install.Path, "CodexTray.App.exe")),
                "CodexTray.exe");
        }
        finally
        {
            AppUpdateWork.DeleteQuietly(Directory.GetParent(handoff.SourceDirectory)?.FullName);
        }

        AppUpdateCheckResult current = await session.CheckAsync(new Version(9, 9, 9), CancellationToken.None);
        AssertEqual(AppUpdateAvailability.UpToDate, current.Availability, "already current");
    }

    /// <summary>
    /// Replaces installation files while preserving settings and unrelated files.
    /// </summary>
    private static Task TestApplyUpdateAsync()
    {
        using TempDirectory temp = new();
        string source = Path.Combine(temp.Path, "source");
        string target = Path.Combine(temp.Path, "target");
        string backup = Path.Combine(temp.Path, "backup");
        WritePackage(source, "new");
        File.WriteAllText(Path.Combine(source, CodexTrayDefaults.SettingsFileName), "package-settings");
        File.WriteAllText(Path.Combine(source, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.LiteMonitorPluginSubdirectory, "added.txt"), "added");
        WritePackage(target, "old");
        File.WriteAllText(Path.Combine(target, CodexTrayDefaults.SettingsFileName), "user-settings");
        File.WriteAllText(Path.Combine(target, "notes.txt"), "keep-me");

        AppUpdateInstaller.Apply(source, target, backup);
        AssertEqual("user-settings", File.ReadAllText(Path.Combine(target, CodexTrayDefaults.SettingsFileName)), "preserved settings");
        AssertEqual("keep-me", File.ReadAllText(Path.Combine(target, "notes.txt")), "preserved extra file");
        AssertEqual("added", File.ReadAllText(Path.Combine(target, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.LiteMonitorPluginSubdirectory, "added.txt")), "added file");
        AssertTrue(File.ReadAllText(Path.Combine(target, "Resources", "icon.png")).StartsWith("new:", StringComparison.Ordinal), "replaced resource");
        AssertTrue(File.ReadAllText(Path.Combine(target, CodexTrayDefaults.AppName + ".exe")).StartsWith("new:", StringComparison.Ordinal), "replaced executable");
        AssertEqual("user-settings", File.ReadAllText(Path.Combine(backup, CodexTrayDefaults.SettingsFileName)), "backed up settings");

        string nested = Path.Combine(target, "nested");
        Directory.CreateDirectory(nested);
        AssertThrows(() => AppUpdateInstaller.Apply(nested, target, Path.Combine(temp.Path, "backup-overlap")), "overlap");
        AssertTrue(File.ReadAllText(Path.Combine(target, CodexTrayDefaults.AppName + ".exe")).StartsWith("new:", StringComparison.Ordinal), "overlap leaves target");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores the backup when the executable cannot be replaced.
    /// </summary>
    private static Task TestRestoreFailedUpdateAsync()
    {
        using TempDirectory temp = new();
        string source = Path.Combine(temp.Path, "source");
        string target = Path.Combine(temp.Path, "target");
        string backup = Path.Combine(temp.Path, "backup");
        WritePackage(source, "new");
        File.WriteAllText(Path.Combine(source, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.LiteMonitorPluginSubdirectory, "added.txt"), "added");
        WritePackage(target, "old");
        File.WriteAllText(Path.Combine(target, CodexTrayDefaults.SettingsFileName), "user-settings");

        AppUpdateException? error = null;
        using (FileStream sourceLock = new(Path.Combine(source, CodexTrayDefaults.AppName + ".exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _ = sourceLock;
            try
            {
                AppUpdateInstaller.Apply(source, target, backup);
            }
            catch (AppUpdateException exception)
            {
                error = exception;
            }
        }

        AssertTrue(error != null && error.CanRestartPreviousVersion, "restart after restore");
        AssertTrue(error!.Message.Contains("restored", StringComparison.Ordinal), "restore message");
        AssertEqual("user-settings", File.ReadAllText(Path.Combine(target, CodexTrayDefaults.SettingsFileName)), "settings after restore");
        AssertTrue(File.ReadAllText(Path.Combine(target, "Resources", "icon.png")).StartsWith("old:", StringComparison.Ordinal), "resource restored");
        AssertTrue(File.ReadAllText(Path.Combine(target, CodexTrayDefaults.AppName + ".exe")).StartsWith("old:", StringComparison.Ordinal), "executable restored");
        AssertTrue(!File.Exists(Path.Combine(target, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.LiteMonitorPluginSubdirectory, "added.txt")), "new file removed");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds and parses the temporary updater arguments.
    /// </summary>
    private static Task TestUpdateArgumentsAsync()
    {
        DateTimeOffset started = DateTimeOffset.Parse("2026-09-24T01:02:03.0000000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        AppUpdateApplyRequest request = new(42, started, @"C:\Program Files\source", @"D:\Codex Tray", @"D:\Codex Tray\CodexTray.exe");
        string[] args = AppUpdateArguments.Build(request);
        AssertTrue(AppUpdateArguments.IsApplyRequest(args), "apply switch");
        AssertTrue(AppUpdateArguments.TryParse(args, out AppUpdateApplyRequest? parsed, out string error) && parsed != null, error);
        AssertEqual(request.WaitForProcessId, parsed!.WaitForProcessId, "wait pid");
        AssertEqual(request.ParentStartedUtc, parsed.ParentStartedUtc, "parent start");
        AssertEqual(request.SourceDirectory, parsed.SourceDirectory, "source");
        AssertEqual(request.TargetDirectory, parsed.TargetDirectory, "target");
        AssertEqual(request.RestartExecutablePath, parsed.RestartExecutablePath, "restart");
        AssertTrue(!AppUpdateArguments.TryParse(["--apply-update", "--source"], out _, out _), "missing value");
        AssertTrue(!AppUpdateArguments.TryParse(["--apply-update", "--wait-pid", "1", "--source", "s", "--target", "t", "--restart", "r"], out _, out _), "missing parent start");
        AssertTrue(!AppUpdateArguments.TryParse(["--apply-update", "--unexpected", "value", "--wait-pid", "1", "--source", "s", "--target", "t", "--restart", "r"], out _, out _), "unknown option");
        AssertTrue(!AppUpdateArguments.IsApplyRequest(["--check"]), "normal launch");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Allows a published folder and rejects one that still has the build output assembly.
    /// </summary>
    private static Task TestRejectDevelopmentBuildAsync()
    {
        using TempDirectory temp = new();
        string published = Path.Combine(temp.Path, "published");
        WritePackage(published, "published");
        string pluginDll = Path.Combine(published, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.TrafficMonitorPluginSubdirectory, CodexTrayDefaults.TrafficMonitorPluginFileName);
        AssertTrue(File.Exists(pluginDll), "plugin dll");
        string publishedExe = Path.Combine(published, CodexTrayDefaults.AppName + ".exe");
        AssertTrue(AppUpdateWork.IsPublishedInstall(publishedExe), "published install");

        string development = Path.Combine(temp.Path, "development");
        WritePackage(development, "development");
        string developmentExe = Path.Combine(development, CodexTrayDefaults.AppName + ".exe");
        File.WriteAllText(Path.Combine(development, CodexTrayDefaults.AppName + ".dll"), "build-output");
        AssertTrue(!AppUpdateWork.IsPublishedInstall(developmentExe), "development install");
        AssertThrows(() => AppUpdateWork.EnsurePublishedInstall(developmentExe), "development build");

        string source = Path.Combine(temp.Path, "source");
        string backup = Path.Combine(temp.Path, "backup");
        WritePackage(source, "new");
        AssertThrows(() => AppUpdateInstaller.Apply(source, development, backup), "development build");
        AssertTrue(File.ReadAllText(developmentExe).StartsWith("development:", StringComparison.Ordinal), "development executable unchanged");
        AssertTrue(!Directory.Exists(backup), "no backup for a development target");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the files required in a release package.
    /// </summary>
    private static void WritePackage(string root, string marker)
    {
        foreach (string relativePath in CodexTrayDefaults.ReleasePackageRelativePaths)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, marker + ":" + relativePath);
        }
    }

    /// <summary>
    /// Zips a valid package tree.
    /// </summary>
    private static byte[] CreatePackageZip(string marker)
    {
        using TempDirectory temp = new();
        string source = Path.Combine(temp.Path, "package");
        WritePackage(source, marker);
        string zipPath = Path.Combine(temp.Path, "package.zip");
        ZipFile.CreateFromDirectory(source, zipPath);
        return File.ReadAllBytes(zipPath);
    }

    /// <summary>
    /// Creates a release record for package verification.
    /// </summary>
    private static AppUpdateRelease CreateRelease(Version version, long size, string sha256)
    {
        string packageName = CodexTrayDefaults.BuildReleasePackageName(version);
        return new AppUpdateRelease(
            version,
            "v" + version.ToString(3),
            "https://github.com/SnowyLake/CodexTray/releases/tag/v" + version.ToString(3),
            packageName,
            new Uri($"https://github.com/SnowyLake/CodexTray/releases/download/v{version.ToString(3)}/{packageName}"),
            size,
            sha256);
    }

    /// <summary>
    /// Builds a minimal latest-release JSON payload.
    /// </summary>
    private static string ReleaseJson(string tag, string assetName, string downloadUrl, long size, string? digest)
    {
        string digestJson = digest == null ? "null" : $"\"sha256:{digest}\"";
        return $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/SnowyLake/CodexTray/releases/tag/{{tag}}",
          "assets": [
            {
              "name": "CodexTray-src.zip",
              "browser_download_url": "https://github.com/SnowyLake/CodexTray/archive/refs/tags/{{tag}}.zip",
              "size": 12,
              "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            },
            {
              "name": "{{assetName}}",
              "browser_download_url": "{{downloadUrl}}",
              "size": {{size}},
              "digest": {{digestJson}}
            }
          ]
        }
        """;
    }

    /// <summary>
    /// Repeats a character into a 64-digit digest.
    /// </summary>
    private static string Sha(string seed)
    {
        char character = seed[0];
        return new string(character, 64);
    }

    /// <summary>
    /// Writes one zip entry.
    /// </summary>
    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(contents));
    }

    /// <summary>
    /// Asserts that an action throws an update error containing the expected text.
    /// </summary>
    private static void AssertThrows(Action action, string expectedText)
    {
        try
        {
            action();
        }
        catch (AppUpdateException exception)
        {
            AssertTrue(exception.Message.Contains(expectedText, StringComparison.Ordinal), $"message '{exception.Message}' contains '{expectedText}'");
            return;
        }

        throw new InvalidOperationException($"expected an update error containing '{expectedText}'");
    }

    /// <summary>
    /// Asserts that an asynchronous action throws an update error containing the expected text.
    /// </summary>
    private static async Task AssertThrowsAsync(Func<Task> action, string expectedText)
    {
        try
        {
            await action();
        }
        catch (AppUpdateException exception)
        {
            AssertTrue(exception.Message.Contains(expectedText, StringComparison.Ordinal), $"message '{exception.Message}' contains '{expectedText}'");
            return;
        }

        throw new InvalidOperationException($"expected an update error containing '{expectedText}'");
    }

    /// <summary>
    /// Asserts that a condition is true.
    /// </summary>
    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Asserts that two values are equal.
    /// </summary>
    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    private sealed class StaticHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> m_Responder;

        /// <summary>
        /// Creates a handler that returns a response from the request.
        /// </summary>
        public StaticHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            m_Responder = responder;
        }

        /// <summary>
        /// Returns the configured response.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(m_Responder(request));
        }
    }
}
