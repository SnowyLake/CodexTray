using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexTray.Core;

/// <summary>
/// Tells whether the running build is older than the latest stable release.
/// </summary>
public enum AppUpdateAvailability
{
    /// <summary>
    /// The running build is the latest stable release or newer.
    /// </summary>
    UpToDate,

    /// <summary>
    /// A newer stable release is available.
    /// </summary>
    UpdateAvailable,
}

/// <summary>
/// A stable GitHub release package that can replace the local installation.
/// </summary>
public sealed record AppUpdateRelease(
    Version Version,
    string TagName,
    string ReleasePageUrl,
    string PackageName,
    Uri PackageUrl,
    long PackageSize,
    string PackageSha256);

/// <summary>
/// The result of comparing the running version with the latest release.
/// </summary>
public sealed record AppUpdateCheckResult(
    AppUpdateAvailability Availability,
    Version CurrentVersion,
    AppUpdateRelease LatestRelease);

/// <summary>
/// Paths used to replace an installation from a temporary copy of the new executable.
/// </summary>
public sealed record AppUpdateHandoff(
    string ApplyExecutablePath,
    string SourceDirectory,
    string TargetDirectory,
    string RestartExecutablePath);

/// <summary>
/// Arguments passed to the temporary executable that applies an update.
/// </summary>
public sealed record AppUpdateApplyRequest(
    int WaitForProcessId,
    DateTimeOffset ParentStartedUtc,
    string SourceDirectory,
    string TargetDirectory,
    string RestartExecutablePath);

/// <summary>
/// Parses stable X.Y.Z versions used by releases and the running assembly.
/// </summary>
public static partial class AppUpdateVersion
{
    /// <summary>
    /// Parses a vX.Y.Z release tag.
    /// </summary>
    public static bool TryParseTag(string? tag, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag) || tag[0] != 'v')
        {
            return false;
        }

        return TryParseNumbers(tag[1..], out version);
    }

    /// <summary>
    /// Parses the assembly informational version when it is exactly X.Y.Z.
    /// </summary>
    public static bool TryParseAssemblyVersion(string? text, out Version? version)
    {
        return TryParseNumbers(text, out version);
    }

    /// <summary>
    /// Returns whether the latest release is newer than the running version.
    /// </summary>
    public static AppUpdateAvailability Compare(Version current, Version latest)
    {
        return latest.CompareTo(current) > 0 ? AppUpdateAvailability.UpdateAvailable : AppUpdateAvailability.UpToDate;
    }

    /// <summary>
    /// Parses an exact three-part numeric version.
    /// </summary>
    private static bool TryParseNumbers(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = StableVersionPattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        version = new Version(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture));
        return true;
    }

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
}

/// <summary>
/// Reads the latest GitHub release payload and selects this app's Windows package.
/// </summary>
public static class AppUpdateReleaseParser
{
    /// <summary>
    /// Parses the latest-release JSON into the win-x64 package described by that release.
    /// </summary>
    public static AppUpdateRelease ParseLatest(string json)
    {
        try
        {
            return ParseLatestCore(json);
        }
        catch (AppUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AppUpdateException("GitHub returned an unexpected release response.", innerException: exception);
        }
    }

    /// <summary>
    /// Parses release JSON after the caller has isolated malformed payloads.
    /// </summary>
    private static AppUpdateRelease ParseLatestCore(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string tag = ReadString(root, "tag_name");
        if (!AppUpdateVersion.TryParseTag(tag, out Version? version) || version == null)
        {
            throw new AppUpdateException($"The latest release tag '{tag}' is not a stable X.Y.Z version.");
        }

        string releasePage = ReadString(root, "html_url");
        if (!IsGitHubHttpsUrl(releasePage, out _))
        {
            throw new AppUpdateException("The release page URL is not a GitHub HTTPS URL.");
        }

        string packageName = CodexTrayDefaults.BuildReleasePackageName(version);
        if (!TryFindAsset(root, packageName, out JsonElement asset))
        {
            throw new AppUpdateException($"The latest release does not include {packageName}.");
        }

        string downloadUrl = ReadString(asset, "browser_download_url");
        if (!IsGitHubHttpsUrl(downloadUrl, out Uri? packageUri) || packageUri == null
            || !packageUri.AbsolutePath.EndsWith("/" + packageName, StringComparison.Ordinal))
        {
            throw new AppUpdateException("The update package URL is not the GitHub release asset.");
        }

        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > CodexTrayDefaults.MaximumReleasePackageBytes)
        {
            throw new AppUpdateException("The update package size is not supported.");
        }

        if (!asset.TryGetProperty("digest", out JsonElement digestElement) || digestElement.ValueKind != JsonValueKind.String
            || !TryParseSha256(digestElement.GetString(), out string sha256))
        {
            throw new AppUpdateException("The update package is missing a SHA-256 digest.");
        }

        return new AppUpdateRelease(version, tag, releasePage, packageName, packageUri, size, sha256);
    }

    /// <summary>
    /// Finds the release asset whose file name is exactly the expected package.
    /// </summary>
    private static bool TryFindAsset(JsonElement root, string packageName, out JsonElement asset)
    {
        asset = default;
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement candidate in assets.EnumerateArray())
        {
            if (candidate.TryGetProperty("name", out JsonElement name)
                && string.Equals(name.GetString(), packageName, StringComparison.Ordinal))
            {
                asset = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a required string property.
    /// </summary>
    private static string ReadString(JsonElement element, string propertyName)
    {
        string? value = element.GetProperty(propertyName).GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppUpdateException("GitHub returned an unexpected release response.");
        }

        return value;
    }

    /// <summary>
    /// Accepts an absolute HTTPS URL hosted on github.com.
    /// </summary>
    private static bool IsGitHubHttpsUrl(string text, out Uri? uri)
    {
        bool accepted = Uri.TryCreate(text, UriKind.Absolute, out uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
        if (!accepted)
        {
            uri = null;
        }

        return accepted;
    }

    /// <summary>
    /// Parses a GitHub sha256 digest into lowercase hex.
    /// </summary>
    private static bool TryParseSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(digest))
        {
            return false;
        }

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || digest.Length != prefix.Length + 64)
        {
            return false;
        }

        string hex = digest[prefix.Length..];
        foreach (char character in hex)
        {
            bool digit = character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!digit)
            {
                return false;
            }
        }

        sha256 = hex.ToLowerInvariant();
        return true;
    }
}

/// <summary>
/// Builds and reads the arguments of the temporary update process.
/// </summary>
public static class AppUpdateArguments
{
    public const string ApplySwitch = "--apply-update";
    public const string WaitPidOption = "--wait-pid";
    public const string ParentStartedUtcOption = "--parent-started-utc";
    public const string SourceOption = "--source";
    public const string TargetOption = "--target";
    public const string RestartOption = "--restart";

    /// <summary>
    /// Returns true when the process was started to apply an update.
    /// </summary>
    public static bool IsApplyRequest(IReadOnlyList<string> args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, ApplySwitch, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the argument list for the temporary update process.
    /// </summary>
    public static string[] Build(AppUpdateApplyRequest request)
    {
        return
        [
            ApplySwitch,
            WaitPidOption,
            request.WaitForProcessId.ToString(CultureInfo.InvariantCulture),
            ParentStartedUtcOption,
            request.ParentStartedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            SourceOption,
            request.SourceDirectory,
            TargetOption,
            request.TargetDirectory,
            RestartOption,
            request.RestartExecutablePath,
        ];
    }

    /// <summary>
    /// Parses an apply-update argument list. The error is safe to show when parsing fails.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out AppUpdateApplyRequest? request, out string error)
    {
        request = null;
        error = "CodexTray could not read the update request.";
        int? waitForProcessId = null;
        DateTimeOffset? parentStartedUtc = null;
        string? source = null;
        string? target = null;
        string? restart = null;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, ApplySwitch, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Count)
            {
                return false;
            }

            string value = args[++index];
            if (value.Length == 0 || value.StartsWith('-'))
            {
                return false;
            }

            if (string.Equals(arg, WaitPidOption, StringComparison.Ordinal))
            {
                if (waitForProcessId != null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
                {
                    return false;
                }

                waitForProcessId = processId;
            }
            else if (string.Equals(arg, ParentStartedUtcOption, StringComparison.Ordinal))
            {
                if (parentStartedUtc != null
                    || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset started))
                {
                    return false;
                }

                parentStartedUtc = started.ToUniversalTime();
            }
            else if (string.Equals(arg, SourceOption, StringComparison.Ordinal))
            {
                if (source != null)
                {
                    return false;
                }

                source = value;
            }
            else if (string.Equals(arg, TargetOption, StringComparison.Ordinal))
            {
                if (target != null)
                {
                    return false;
                }

                target = value;
            }
            else if (string.Equals(arg, RestartOption, StringComparison.Ordinal))
            {
                if (restart != null)
                {
                    return false;
                }

                restart = value;
            }
            else
            {
                return false;
            }
        }

        if (waitForProcessId == null || parentStartedUtc == null || source == null || target == null || restart == null)
        {
            return false;
        }

        request = new AppUpdateApplyRequest(waitForProcessId.Value, parentStartedUtc.Value, source, target, restart);
        error = string.Empty;
        return true;
    }
}
