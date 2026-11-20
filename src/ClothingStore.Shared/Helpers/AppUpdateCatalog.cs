using System.Globalization;
using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Shared.Helpers;

public sealed record AppUpdateReleaseInfo(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string ReleaseTitle,
    string ReleaseNotes,
    string ReleasePageUrl,
    DateTimeOffset? PublishedAt,
    string? WindowsDownloadUrl,
    long? WindowsAssetSizeBytes,
    string? AndroidDownloadUrl,
    long? AndroidAssetSizeBytes,
    string? SelectedAssetName,
    string? SelectedDownloadUrl,
    long? SelectedAssetSizeBytes,
    string Source,
    string? SelectedApiDownloadUrl = null);

/// <summary>
/// Catálogo centralizado de versão do YouLink, parser de GitHub Releases e utilitários
/// puros para atualização direta dentro do aplicativo no Windows e Android.
/// </summary>
public static class AppUpdateCatalog
{
    public const string CurrentAppVersion = "1.0.59";
    public const string DefaultCentralPlatformServerUrl = "https://youlink-loja.onrender.com";
    public const string GitHubOwner = "giovannipieronisleal-pixel";
    public const string GitHubRepo = "yourlink";
    public const string GitHubLatestReleaseApiUrl =
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    public const string ReleasesPageUrl =
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public const string WindowsAssetName = "YouLink-Windows-x64.zip";
    public const string WindowsSetupAssetName = "YouLink-Setup-x64.exe";
    public const string AndroidAssetName = "YouLink-Android.apk";
    public const string WindowsLauncherName = "YouLink.exe";
    public const string WindowsExecutableName = "appmaui.blazor.exe";

    public static string NormalizeVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return string.Empty;

        var trimmed = rawVersion.Trim().TrimStart('v', 'V');
        var separatorIndex = trimmed.IndexOfAny(['-', '+', ' ']);
        if (separatorIndex > 0)
        {
            trimmed = trimmed[..separatorIndex];
        }

        if (!Version.TryParse(trimmed, out var parsed))
            return trimmed;

        return parsed.Revision > 0
            ? parsed.ToString()
            : $"{parsed.Major}.{Math.Max(0, parsed.Minor)}.{Math.Max(0, parsed.Build)}";
    }

    private static Version NormalizeComparableVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static bool IsVersionNewer(string? candidateVersion, string? currentVersion)
    {
        var normalizedCandidate = NormalizeVersion(candidateVersion);
        var normalizedCurrent = NormalizeVersion(currentVersion);

        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedCurrent))
            return false;

        return Version.TryParse(normalizedCandidate, out var candidate)
            && Version.TryParse(normalizedCurrent, out var current)
            && NormalizeComparableVersion(candidate) > NormalizeComparableVersion(current);
    }

    public static bool IsWindowsSetupInstallation(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        try
        {
            var trimmed = baseDirectory.TrimEnd('\\', '/');
            if (File.Exists(Path.Combine(trimmed, "unins000.exe")))
                return true;

            var parent = Directory.GetParent(trimmed);
            return parent is not null && File.Exists(Path.Combine(parent.FullName, "unins000.exe"));
        }
        catch
        {
            return false;
        }
    }

    public static AppUpdateReleaseInfo? ParseGitHubReleaseJson(
        string json,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup = false)
        => ParseGitHubReleaseJson(json, currentVersion, isWindows, isAndroid, preferWindowsSetup, preferApiAssetUrl: false);

    public static AppUpdateReleaseInfo? ParseGitHubReleaseJson(
        string json,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup,
        bool preferApiAssetUrl,
        string defaultTitlePrefix = "YouLink",
        string source = "GitHubReleases")
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                return null;

            var tagName = TryGetOptionalString(root, "tag_name");
            var normalizedLatest = NormalizeVersion(tagName);
            if (string.IsNullOrWhiteSpace(normalizedLatest))
                return null;

            var normalizedCurrent = NormalizeVersion(currentVersion);
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                normalizedCurrent = CurrentAppVersion;

            var releaseName = TryGetOptionalString(root, "name");
            var releaseBody = TryGetOptionalString(root, "body");
            var htmlUrl = TryGetOptionalString(root, "html_url");

            DateTimeOffset? publishedAt = null;
            var pubText = TryGetOptionalString(root, "published_at");
            if (!string.IsNullOrWhiteSpace(pubText) &&
                DateTimeOffset.TryParse(pubText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                publishedAt = parsedDate;
            }

            string? windowsSetupUrl = null;
            string? windowsSetupApiUrl = null;
            string? windowsSetupName = null;
            long? windowsSetupSize = null;
            string? windowsUrl = null;
            string? windowsApiUrl = null;
            string? windowsName = null;
            long? windowsSize = null;
            string? androidUrl = null;
            string? androidApiUrl = null;
            string? androidName = null;
            long? androidSize = null;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var assetName = TryGetOptionalString(asset, "name");
                    var downloadUrl = TryGetOptionalString(asset, "browser_download_url");
                    var apiUrl = TryGetOptionalString(asset, "url");
                    var effectiveUrl = !string.IsNullOrWhiteSpace(downloadUrl) ? downloadUrl : apiUrl;
                    long? size = TryGetOptionalInt64(asset, "size");

                    if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(effectiveUrl))
                        continue;

                    if (IsWindowsSetupAssetName(assetName))
                    {
                        windowsSetupUrl ??= effectiveUrl;
                        windowsSetupApiUrl ??= apiUrl;
                        windowsSetupName ??= assetName;
                        windowsSetupSize ??= size;
                    }
                    else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        windowsUrl ??= effectiveUrl;
                        windowsApiUrl ??= apiUrl;
                        windowsName ??= assetName;
                        windowsSize ??= size;
                    }
                    else if (assetName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        androidUrl ??= effectiveUrl;
                        androidApiUrl ??= apiUrl;
                        androidName ??= assetName;
                        androidSize ??= size;
                    }
                }
            }

            var (selectedName, selectedUrl, selectedSize) = SelectPlatformAsset(
                isWindows,
                isAndroid,
                preferWindowsSetup,
                windowsSetupUrl,
                windowsSetupSize,
                windowsUrl,
                windowsSize,
                androidUrl,
                androidSize,
                windowsSetupAssetName: windowsSetupName,
                windowsAssetName: windowsName,
                androidAssetName: androidName);

            var (_, selectedApiUrl, _) = preferApiAssetUrl
                ? SelectPlatformAsset(
                    isWindows,
                    isAndroid,
                    preferWindowsSetup,
                    windowsSetupApiUrl ?? windowsSetupUrl,
                    windowsSetupSize,
                    windowsApiUrl ?? windowsUrl,
                    windowsSize,
                    androidApiUrl ?? androidUrl,
                    androidSize,
                    windowsSetupAssetName: windowsSetupName,
                    windowsAssetName: windowsName,
                    androidAssetName: androidName)
                : (null, null, null);

            return new AppUpdateReleaseInfo(
                CurrentVersion: normalizedCurrent,
                LatestVersion: normalizedLatest,
                IsUpdateAvailable: IsVersionNewer(normalizedLatest, normalizedCurrent),
                ReleaseTitle: string.IsNullOrWhiteSpace(releaseName) ? $"{defaultTitlePrefix} v{normalizedLatest}" : releaseName.Trim(),
                ReleaseNotes: string.IsNullOrWhiteSpace(releaseBody)
                    ? "Melhorias de desempenho, sincronização online 24h e correções visuais."
                    : releaseBody.Trim(),
                ReleasePageUrl: string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUrl : htmlUrl.Trim(),
                PublishedAt: publishedAt,
                WindowsDownloadUrl: preferWindowsSetup ? (windowsSetupUrl ?? windowsUrl) : (windowsUrl ?? windowsSetupUrl),
                WindowsAssetSizeBytes: preferWindowsSetup ? (windowsSetupSize ?? windowsSize) : (windowsSize ?? windowsSetupSize),
                AndroidDownloadUrl: androidUrl,
                AndroidAssetSizeBytes: androidSize,
                SelectedAssetName: selectedName,
                SelectedDownloadUrl: selectedUrl,
                SelectedAssetSizeBytes: selectedSize,
                Source: source,
                SelectedApiDownloadUrl: selectedApiUrl);
        }
        catch
        {
            return null;
        }
    }

    public static AppUpdateReleaseInfo? ParseCustomUpdateManifestJson(
        string json,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        string defaultTitlePrefix,
        string defaultFallbackNotes,
        bool preferWindowsSetup = false,
        string source = "ManifestJson")
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("tag_name", out _))
            {
                return ParseGitHubReleaseJson(
                    json,
                    currentVersion,
                    isWindows,
                    isAndroid,
                    preferWindowsSetup,
                    preferApiAssetUrl: false,
                    defaultTitlePrefix: defaultTitlePrefix,
                    source: source);
            }

            var rawVersion = TryGetOptionalString(root, "version") ?? TryGetOptionalString(root, "latestVersion");
            var normalizedLatest = NormalizeVersion(rawVersion);
            if (string.IsNullOrWhiteSpace(normalizedLatest) || !Version.TryParse(normalizedLatest, out _))
                return null;

            var normalizedCurrent = NormalizeVersion(currentVersion);
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
                normalizedCurrent = CurrentAppVersion;

            var title = TryGetOptionalString(root, "title") ?? TryGetOptionalString(root, "name");
            var notes = TryGetOptionalString(root, "notes") ?? TryGetOptionalString(root, "body");
            var releasePageUrl = TryGetOptionalString(root, "releasePageUrl") ?? TryGetOptionalString(root, "html_url") ?? string.Empty;

            DateTimeOffset? publishedAt = null;
            var pubText = TryGetOptionalString(root, "publishedAt") ?? TryGetOptionalString(root, "published_at");
            if (!string.IsNullOrWhiteSpace(pubText) &&
                DateTimeOffset.TryParse(pubText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                publishedAt = parsedDate;
            }

            var windowsSetupUrl = TryGetOptionalString(root, "windowsSetupUrl");
            var windowsSetupSize = TryGetOptionalInt64(root, "windowsSetupSizeBytes");
            var windowsUrl = TryGetOptionalString(root, "windowsUrl");
            var windowsSize = TryGetOptionalInt64(root, "windowsSizeBytes");
            var androidUrl = TryGetOptionalString(root, "androidUrl");
            var androidSize = TryGetOptionalInt64(root, "androidSizeBytes");

            var (selectedName, selectedUrl, selectedSize) = SelectPlatformAsset(
                isWindows,
                isAndroid,
                preferWindowsSetup,
                windowsSetupUrl,
                windowsSetupSize,
                windowsUrl,
                windowsSize,
                androidUrl,
                androidSize,
                windowsSetupAssetName: ExtractAssetName(windowsSetupUrl, WindowsSetupAssetName),
                windowsAssetName: ExtractAssetName(windowsUrl, WindowsAssetName),
                androidAssetName: ExtractAssetName(androidUrl, AndroidAssetName));

            return new AppUpdateReleaseInfo(
                CurrentVersion: normalizedCurrent,
                LatestVersion: normalizedLatest,
                IsUpdateAvailable: IsVersionNewer(normalizedLatest, normalizedCurrent),
                ReleaseTitle: string.IsNullOrWhiteSpace(title) ? $"{defaultTitlePrefix} v{normalizedLatest}" : title.Trim(),
                ReleaseNotes: string.IsNullOrWhiteSpace(notes) ? defaultFallbackNotes : notes.Trim(),
                ReleasePageUrl: releasePageUrl.Trim(),
                PublishedAt: publishedAt,
                WindowsDownloadUrl: preferWindowsSetup ? (windowsSetupUrl ?? windowsUrl) : (windowsUrl ?? windowsSetupUrl),
                WindowsAssetSizeBytes: preferWindowsSetup ? (windowsSetupSize ?? windowsSize) : (windowsSize ?? windowsSetupSize),
                AndroidDownloadUrl: androidUrl,
                AndroidAssetSizeBytes: androidSize,
                SelectedAssetName: selectedName,
                SelectedDownloadUrl: selectedUrl,
                SelectedAssetSizeBytes: selectedSize,
                Source: source);
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveManifestAssetUrl(string? manifestUrl, string? candidateUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl))
            return null;

        var trimmed = candidateUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var abs))
            return abs.ToString();

        if (!string.IsNullOrWhiteSpace(manifestUrl) &&
            Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, trimmed, out var combined))
        {
            return combined.ToString();
        }

        return trimmed;
    }

    public static AppUpdateReleaseInfo? ParseGitHubRedirectLocation(
        string? redirectLocationOrUrl,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup = false)
    {
        if (string.IsNullOrWhiteSpace(redirectLocationOrUrl))
            return null;

        var trimmed = redirectLocationOrUrl.Trim();
        var tagMarkerIndex = trimmed.LastIndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
        if (tagMarkerIndex < 0)
            return null;

        var rawTag = trimmed[(tagMarkerIndex + "/releases/tag/".Length)..];
        var queryOrFragment = rawTag.IndexOfAny(['?', '#', '/']);
        if (queryOrFragment >= 0)
        {
            rawTag = rawTag[..queryOrFragment];
        }

        var latestVersion = NormalizeVersion(Uri.UnescapeDataString(rawTag));
        if (string.IsNullOrWhiteSpace(latestVersion) || !Version.TryParse(latestVersion, out _))
            return null;

        var releasePageUrl = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/tag/v{latestVersion}";

        return BuildDeterministicReleaseInfo(
            latestVersion: latestVersion,
            currentVersion: currentVersion,
            isWindows: isWindows,
            isAndroid: isAndroid,
            preferWindowsSetup: preferWindowsSetup,
            releaseNotes: "Nova versão oficial disponível no GitHub Releases do YouLink.",
            releasePageUrl: releasePageUrl,
            source: "GitHubRedirect");
    }

    public static AppUpdateReleaseInfo? MergeWithApiCompatibility(
        AppUpdateReleaseInfo? githubRelease,
        ApiCompatibilityStatusDto? apiCompat,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup = false)
    {
        var normalizedCurrent = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
            normalizedCurrent = CurrentAppVersion;

        if (apiCompat is null || string.IsNullOrWhiteSpace(apiCompat.LatestAppVersion))
            return githubRelease;

        var apiLatest = NormalizeVersion(apiCompat.LatestAppVersion);
        if (githubRelease is not null && !IsVersionNewer(apiLatest, githubRelease.LatestVersion))
            return githubRelease;

        var releasePage = string.IsNullOrWhiteSpace(apiCompat.ReleaseUrl)
            ? ReleasesPageUrl
            : apiCompat.ReleaseUrl.Trim();

        return BuildDeterministicReleaseInfo(
            latestVersion: apiLatest,
            currentVersion: normalizedCurrent,
            isWindows: isWindows,
            isAndroid: isAndroid,
            preferWindowsSetup: preferWindowsSetup,
            releaseNotes: string.IsNullOrWhiteSpace(apiCompat.Message)
                ? "Nova atualização oficial disponível."
                : apiCompat.Message.Trim(),
            releasePageUrl: releasePage,
            source: "ApiCompatibility");
    }

    private static AppUpdateReleaseInfo BuildDeterministicReleaseInfo(
        string latestVersion,
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup,
        string releaseNotes,
        string releasePageUrl,
        string source)
    {
        var normalizedLatest = NormalizeVersion(latestVersion);
        var normalizedCurrent = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
            normalizedCurrent = CurrentAppVersion;

        var tagBase = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/v{normalizedLatest}";
        var windowsSetupUrl = $"{tagBase}/{WindowsSetupAssetName}";
        var windowsUrl = $"{tagBase}/{WindowsAssetName}";
        var androidUrl = $"{tagBase}/{AndroidAssetName}";
        var (selectedName, selectedUrl, selectedSize) = SelectPlatformAsset(
            isWindows,
            isAndroid,
            preferWindowsSetup,
            windowsSetupUrl,
            null,
            windowsUrl,
            null,
            androidUrl,
            null);

        return new AppUpdateReleaseInfo(
            CurrentVersion: normalizedCurrent,
            LatestVersion: normalizedLatest,
            IsUpdateAvailable: IsVersionNewer(normalizedLatest, normalizedCurrent),
            ReleaseTitle: $"YouLink v{normalizedLatest}",
            ReleaseNotes: releaseNotes,
            ReleasePageUrl: releasePageUrl,
            PublishedAt: null,
            WindowsDownloadUrl: preferWindowsSetup ? windowsSetupUrl : windowsUrl,
            WindowsAssetSizeBytes: null,
            AndroidDownloadUrl: androidUrl,
            AndroidAssetSizeBytes: null,
            SelectedAssetName: selectedName,
            SelectedDownloadUrl: selectedUrl,
            SelectedAssetSizeBytes: selectedSize,
            Source: source);
    }

    public static string FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value <= 0)
            return "Tamanho automático";

        var mb = bytes.Value / (1024d * 1024d);
        if (mb >= 1d)
            return $"{mb.ToString("N1", CultureInfo.GetCultureInfo("pt-BR"))} MB";

        var kb = Math.Max(1d, bytes.Value / 1024d);
        return $"{kb.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} KB";
    }

    public static string? ResolveExtractedAppRoot(
        string extractedDirectory,
        string executableFileName = WindowsExecutableName)
    {
        if (string.IsNullOrWhiteSpace(extractedDirectory) || !Directory.Exists(extractedDirectory))
            return null;

        var directCandidate = Path.Combine(extractedDirectory, executableFileName);
        if (File.Exists(directCandidate))
            return extractedDirectory;

        var match = Directory
            .EnumerateFiles(extractedDirectory, executableFileName, SearchOption.AllDirectories)
            .FirstOrDefault();

        return match is null ? null : Path.GetDirectoryName(match);
    }

    public static bool IsDevelopmentBuildDirectory(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        var normalized = baseDirectory.Replace('/', '\\');
        return normalized.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildWindowsApplyUpdateScript(
        int targetProcessId,
        string sourceAppDir,
        string targetAppDir,
        string executableFileName,
        string logFilePath,
        string? launcherFileName = null,
        string? appDisplayName = null,
        string? updaterCacheRootDir = null)
    {
        var effectiveDisplay = string.IsNullOrWhiteSpace(appDisplayName) ? "YouLink" : appDisplayName.Trim();
        var effectiveLauncher = string.IsNullOrWhiteSpace(launcherFileName) ? WindowsLauncherName : launcherFileName.Trim();
        var normalizedCacheRoot = string.IsNullOrWhiteSpace(updaterCacheRootDir)
            ? string.Empty
            : updaterCacheRootDir.TrimEnd('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$targetPid = {targetProcessId}");
        sb.AppendLine($"$sourceDir = '{EscapePowerShellSingleQuoted(sourceAppDir.TrimEnd('\\', '/'))}'");
        sb.AppendLine($"$targetDir = '{EscapePowerShellSingleQuoted(targetAppDir.TrimEnd('\\', '/'))}'");
        sb.AppendLine($"$exeName = '{EscapePowerShellSingleQuoted(executableFileName)}'");
        sb.AppendLine($"$launcherName = '{EscapePowerShellSingleQuoted(effectiveLauncher)}'");
        sb.AppendLine($"$logPath = '{EscapePowerShellSingleQuoted(logFilePath)}'");
        sb.AppendLine($"$updaterRootDir = '{EscapePowerShellSingleQuoted(normalizedCacheRoot)}'");
        sb.AppendLine("$updateWorkDir = Split-Path -Path $logPath -Parent");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -Path $logPath -Value \"[$(Get-Date -Format 'HH:mm:ss')] $msg\" -Encoding UTF8 }");
        sb.AppendLine($"Write-Log 'Iniciando atualização automática do {EscapePowerShellSingleQuoted(effectiveDisplay)} Windows...'");
        sb.AppendLine("for ($i = 0; $i -lt 40; $i++) {");
        sb.AppendLine("    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($null -eq $proc) { break }");
        sb.AppendLine("    Start-Sleep -Milliseconds 350");
        sb.AppendLine("}");
        sb.AppendLine("$stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($null -ne $stillRunning) { try { Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue } catch {} Start-Sleep -Milliseconds 600 }");
        sb.AppendLine("$targetInstallRoot = if ((Split-Path -Leaf $targetDir) -eq 'App') { Split-Path -Parent $targetDir } else { $targetDir }");
        sb.AppendLine("$procNamesToStop = @([System.IO.Path]::GetFileNameWithoutExtension($exeName), [System.IO.Path]::GetFileNameWithoutExtension($launcherName)) | Where-Object { $_ } | Select-Object -Unique");
        sb.AppendLine("foreach ($pName in $procNamesToStop) {");
        sb.AppendLine("    Get-Process -Name $pName -ErrorAction SilentlyContinue | ForEach-Object {");
        sb.AppendLine("        try {");
        sb.AppendLine("            $procPath = $_.Path");
        sb.AppendLine("            if (-not $procPath -or $procPath.StartsWith($targetInstallRoot, [System.StringComparison]::OrdinalIgnoreCase)) {");
        sb.AppendLine("                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("            }");
        sb.AppendLine("        } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 700");
        sb.AppendLine("$robocopyArgs = @($sourceDir, $targetDir, '/E', '/IS', '/IT', '/R:6', '/W:1', '/XD', 'App_Data', 'EBWebView', '/XF', 'clothingstore_db.json', 'platform_admin_db.json', 'platform_owner_settings.json', 'email_outbox.txt', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')");
        sb.AppendLine("& robocopy @robocopyArgs | Out-Null");
        sb.AppendLine("$rc = $LASTEXITCODE");
        sb.AppendLine("Write-Log \"Robocopy exit code: $rc\"");
        sb.AppendLine("if ($rc -ge 8) { throw \"Falha ao copiar arquivos da atualização (robocopy=$rc).\" }");
        sb.AppendLine("$sourceParent = Split-Path -Path $sourceDir -Parent");
        sb.AppendLine("$targetParent = Split-Path -Path $targetDir -Parent");
        sb.AppendLine("$launchExe = Join-Path $targetDir $exeName");
        sb.AppendLine("$launchWorkDir = $targetDir");
        sb.AppendLine("if ((Split-Path -Leaf $targetDir) -eq 'App' -and $targetParent -and $sourceParent) {");
        sb.AppendLine("    $srcLauncher = Join-Path $sourceParent $launcherName");
        sb.AppendLine("    if (Test-Path $srcLauncher) {");
        sb.AppendLine("        Copy-Item -Path $srcLauncher -Destination (Join-Path $targetParent $launcherName) -Force");
        sb.AppendLine("        $launchExe = Join-Path $targetParent $launcherName");
        sb.AppendLine("    }");
        sb.AppendLine("    $srcReadme = Join-Path $sourceParent 'LEIA-ME.txt'");
        sb.AppendLine("    if (Test-Path $srcReadme) { Copy-Item -Path $srcReadme -Destination (Join-Path $targetParent 'LEIA-ME.txt') -Force }");
        sb.AppendLine("}");
        sb.AppendLine("if (Test-Path $launchExe) {");
        sb.AppendLine("    Write-Log \"Reabrindo $launchExe\"");
        sb.AppendLine("    Start-Process -FilePath $launchExe -WorkingDirectory $launchWorkDir");
        sb.AppendLine("}");
        AppendWindowsPostUpdateCleanupScript(sb, "$sourceDir", isDirectoryArtifact: true);
        return sb.ToString();
    }

    public static string BuildWindowsInstallerUpdateScript(
        int targetProcessId,
        string setupExePath,
        string targetLauncherPath,
        string logFilePath,
        string? appDisplayName = null,
        string? updaterCacheRootDir = null)
    {
        var effectiveDisplay = string.IsNullOrWhiteSpace(appDisplayName) ? "YouLink" : appDisplayName.Trim();
        var setupName = Path.GetFileName(setupExePath);
        var normalizedCacheRoot = string.IsNullOrWhiteSpace(updaterCacheRootDir)
            ? string.Empty
            : updaterCacheRootDir.TrimEnd('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$targetPid = {targetProcessId}");
        sb.AppendLine($"$setupExe = '{EscapePowerShellSingleQuoted(setupExePath)}'");
        sb.AppendLine($"$launchExe = '{EscapePowerShellSingleQuoted(targetLauncherPath)}'");
        sb.AppendLine($"$logPath = '{EscapePowerShellSingleQuoted(logFilePath)}'");
        sb.AppendLine($"$updaterRootDir = '{EscapePowerShellSingleQuoted(normalizedCacheRoot)}'");
        sb.AppendLine("$updateWorkDir = Split-Path -Path $logPath -Parent");
        sb.AppendLine("function Write-Log([string]$msg) { Add-Content -Path $logPath -Value \"[$(Get-Date -Format 'HH:mm:ss')] $msg\" -Encoding UTF8 }");
        sb.AppendLine($"Write-Log 'Iniciando atualização do {EscapePowerShellSingleQuoted(effectiveDisplay)} pelo instalador {EscapePowerShellSingleQuoted(setupName)}...'");
        sb.AppendLine("for ($i = 0; $i -lt 40; $i++) {");
        sb.AppendLine("    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($null -eq $proc) { break }");
        sb.AppendLine("    Start-Sleep -Milliseconds 350");
        sb.AppendLine("}");
        sb.AppendLine("$stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($null -ne $stillRunning) { try { Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue } catch {} Start-Sleep -Milliseconds 600 }");
        sb.AppendLine("$launchDir = Split-Path -Parent $launchExe");
        sb.AppendLine("$installRoot = if ((Split-Path -Leaf $launchDir) -eq 'App') { Split-Path -Parent $launchDir } else { $launchDir }");
        sb.AppendLine("$procNamesToStop = @('appmaui.blazor', 'YouLink', [System.IO.Path]::GetFileNameWithoutExtension($launchExe)) | Where-Object { $_ } | Select-Object -Unique");
        sb.AppendLine("foreach ($pName in $procNamesToStop) {");
        sb.AppendLine("    Get-Process -Name $pName -ErrorAction SilentlyContinue | ForEach-Object {");
        sb.AppendLine("        try {");
        sb.AppendLine("            $procPath = $_.Path");
        sb.AppendLine("            if (-not $procPath -or ($installRoot -and $procPath.StartsWith($installRoot, [System.StringComparison]::OrdinalIgnoreCase))) {");
        sb.AppendLine("                & taskkill.exe /F /T /PID $($_.Id) | Out-Null");
        sb.AppendLine("            }");
        sb.AppendLine("        } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 800");
        sb.AppendLine("$innoLogPath = Join-Path (Split-Path -Parent $logPath) 'setup-installer.log'");
        sb.AppendLine("$procSetup = Start-Process -FilePath $setupExe -ArgumentList @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS', '/FORCECLOSEAPPLICATIONS', \"/LOG=$innoLogPath\") -Wait -PassThru");
        sb.AppendLine("Write-Log \"Instalador finalizado com código: $($procSetup.ExitCode)\"");
        sb.AppendLine("if ($procSetup.ExitCode -ne 0) {");
        sb.AppendLine("    Write-Log \"Falha no instalador (ExitCode=$($procSetup.ExitCode)). Veja $innoLogPath\"");
        sb.AppendLine("    if ([Environment]::UserInteractive) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            Add-Type -AssemblyName System.Windows.Forms");
        sb.AppendLine("            [void][System.Windows.Forms.MessageBox]::Show(\"A atualização automática não pôde ser concluída (código $($procSetup.ExitCode)). Feche todas as janelas do aplicativo e execute novamente o instalador em: $setupExe\", 'YouLink - Atualização', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)");
        sb.AppendLine("        } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    throw \"Instalador retornou código $($procSetup.ExitCode).\"");
        sb.AppendLine("}");
        sb.AppendLine("if (Test-Path $launchExe) {");
        sb.AppendLine("    Start-Process -FilePath $launchExe -WorkingDirectory (Split-Path -Parent $launchExe)");
        sb.AppendLine("}");
        AppendWindowsPostUpdateCleanupScript(sb, "$setupExe", isDirectoryArtifact: false);
        return sb.ToString();
    }

    private static void AppendWindowsPostUpdateCleanupScript(
        StringBuilder sb,
        string primaryArtifactVar,
        bool isDirectoryArtifact)
    {
        sb.AppendLine("Start-Sleep -Milliseconds 800");
        var recurseFlag = isDirectoryArtifact ? " -Recurse" : string.Empty;
        sb.AppendLine($"try {{ if (Test-Path {primaryArtifactVar}) {{ Remove-Item -Path {primaryArtifactVar}{recurseFlag} -Force -ErrorAction SilentlyContinue }} }} catch {{}}");
        sb.AppendLine("if ($updateWorkDir -and (Test-Path $updateWorkDir)) {");
        sb.AppendLine("    $stagingDir = Join-Path $updateWorkDir 'staging'");
        sb.AppendLine("    try { if (Test-Path $stagingDir) { Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue } } catch {}");
        sb.AppendLine("    try { Get-ChildItem -Path $updateWorkDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.zip','.exe','.apk','.part','.tmp' } | Remove-Item -Force -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("}");
        sb.AppendLine("if ($updaterRootDir -and (Test-Path $updaterRootDir)) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        Get-ChildItem -Path $updaterRootDir -Force -ErrorAction SilentlyContinue | Where-Object { -not $updateWorkDir -or -not $_.FullName.Equals($updateWorkDir, [System.StringComparison]::OrdinalIgnoreCase) } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("}");
    }

    /// <summary>
    /// Remove completamente todos os pacotes/instaladores e pastas temporárias do cache de atualização
    /// antes de um novo download ou quando uma atualização pendente é descartada.
    /// </summary>
    public static long PurgeUpdaterCache(string? updaterCacheRootDir)
    {
        if (string.IsNullOrWhiteSpace(updaterCacheRootDir) || !Directory.Exists(updaterCacheRootDir))
            return 0;

        long freedBytes = 0;

        foreach (var file in SafeEnumerateFiles(updaterCacheRootDir))
        {
            freedBytes += TryDeleteFileWithBytes(file);
        }

        foreach (var subDir in SafeEnumerateDirectories(updaterCacheRootDir))
        {
            freedBytes += TryDeleteDirectoryWithBytes(subDir);
        }

        return freedBytes;
    }

    /// <summary>
    /// Remove pacotes antigos já instalados, downloads parciais (.part/.tmp) e arquivos soltos do cache de atualização
    /// para evitar consumo crescente de armazenamento após sucessivas atualizações.
    /// </summary>
    public static long CleanupStaleUpdateArtifacts(
        string? updaterCacheRootDir,
        string? currentVersion,
        string? latestKnownVersion = null,
        string? activePackagePath = null,
        bool preserveActivePackage = false,
        TimeSpan? maxPendingPackageAge = null)
    {
        if (string.IsNullOrWhiteSpace(updaterCacheRootDir) || !Directory.Exists(updaterCacheRootDir))
            return 0;

        long freedBytes = 0;
        var normalizedCurrent = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
            normalizedCurrent = CurrentAppVersion;

        var normalizedLatest = NormalizeVersion(latestKnownVersion);
        var fullActivePackage = TryGetFullNormalizedPath(activePackagePath);
        var fullActiveDir = string.IsNullOrWhiteSpace(fullActivePackage)
            ? null
            : TryGetFullNormalizedPath(Path.GetDirectoryName(fullActivePackage));

        foreach (var file in SafeEnumerateFiles(updaterCacheRootDir))
        {
            var fullFile = TryGetFullNormalizedPath(file);
            if (preserveActivePackage &&
                !string.IsNullOrWhiteSpace(fullActivePackage) &&
                string.Equals(fullFile, fullActivePackage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            freedBytes += TryDeleteFileWithBytes(file);
        }

        var cutoffUtc = maxPendingPackageAge.HasValue
            ? DateTime.UtcNow - maxPendingPackageAge.Value
            : (DateTime?)null;

        foreach (var subDir in SafeEnumerateDirectories(updaterCacheRootDir))
        {
            var fullSubDir = TryGetFullNormalizedPath(subDir);
            var isActiveSubDir = preserveActivePackage &&
                !string.IsNullOrWhiteSpace(fullActiveDir) &&
                string.Equals(fullSubDir, fullActiveDir, StringComparison.OrdinalIgnoreCase);

            if (isActiveSubDir)
            {
                foreach (var nestedFile in SafeEnumerateFiles(subDir))
                {
                    var fullNested = TryGetFullNormalizedPath(nestedFile);
                    if (!string.Equals(fullNested, fullActivePackage, StringComparison.OrdinalIgnoreCase) &&
                        (nestedFile.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                         nestedFile.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                    {
                        freedBytes += TryDeleteFileWithBytes(nestedFile);
                    }
                }
                continue;
            }

            var folderName = Path.GetFileName(subDir);
            var dirVersion = NormalizeVersion(folderName);
            var hasValidVersion = Version.TryParse(dirVersion, out _);

            var shouldDelete = !hasValidVersion
                || !IsVersionNewer(dirVersion, normalizedCurrent)
                || (!string.IsNullOrWhiteSpace(normalizedLatest) &&
                    !string.Equals(dirVersion, normalizedLatest, StringComparison.OrdinalIgnoreCase));

            if (!shouldDelete && cutoffUtc.HasValue)
            {
                try
                {
                    var lastWriteUtc = Directory.GetLastWriteTimeUtc(subDir);
                    if (lastWriteUtc <= cutoffUtc.Value)
                        shouldDelete = true;
                }
                catch
                {
                }
            }

            if (shouldDelete)
            {
                freedBytes += TryDeleteDirectoryWithBytes(subDir);
                continue;
            }

            var stagingDir = Path.Combine(subDir, "staging");
            if (Directory.Exists(stagingDir))
                freedBytes += TryDeleteDirectoryWithBytes(stagingDir);

            foreach (var nestedFile in SafeEnumerateFiles(subDir))
            {
                if (nestedFile.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    nestedFile.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    freedBytes += TryDeleteFileWithBytes(nestedFile);
                }
            }
        }

        return freedBytes;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? TryGetFullNormalizedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static long TryDeleteFileWithBytes(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return 0;

            var length = new FileInfo(filePath).Length;
            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);
            return length;
        }
        catch
        {
            return 0;
        }
    }

    private static long TryDeleteDirectoryWithBytes(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath))
                return 0;

            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            Directory.Delete(dirPath, recursive: true);
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static (string? AssetName, string? DownloadUrl, long? SizeBytes) SelectPlatformAsset(
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup,
        string? windowsSetupUrl,
        long? windowsSetupSize,
        string? windowsUrl,
        long? windowsSize,
        string? androidUrl,
        long? androidSize,
        string? windowsSetupAssetName = null,
        string? windowsAssetName = null,
        string? androidAssetName = null)
    {
        if (isWindows)
        {
            if (preferWindowsSetup && !string.IsNullOrWhiteSpace(windowsSetupUrl))
                return (string.IsNullOrWhiteSpace(windowsSetupAssetName) ? WindowsSetupAssetName : windowsSetupAssetName, windowsSetupUrl, windowsSetupSize);

            if (!string.IsNullOrWhiteSpace(windowsUrl))
                return (string.IsNullOrWhiteSpace(windowsAssetName) ? WindowsAssetName : windowsAssetName, windowsUrl, windowsSize);

            if (!string.IsNullOrWhiteSpace(windowsSetupUrl))
                return (string.IsNullOrWhiteSpace(windowsSetupAssetName) ? WindowsSetupAssetName : windowsSetupAssetName, windowsSetupUrl, windowsSetupSize);
        }

        if (isAndroid && !string.IsNullOrWhiteSpace(androidUrl))
            return (string.IsNullOrWhiteSpace(androidAssetName) ? AndroidAssetName : androidAssetName, androidUrl, androidSize);

        return (null, null, null);
    }

    private static bool IsWindowsSetupAssetName(string assetName) =>
        string.Equals(assetName, WindowsSetupAssetName, StringComparison.OrdinalIgnoreCase)
        || assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetOptionalString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static long? TryGetOptionalInt64(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var prop) && prop.TryGetInt64(out var val)
            ? val
            : null;

    private static string? ExtractAssetName(string? urlOrPath, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
            return null;

        try
        {
            var trimmed = urlOrPath.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var fileName = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
            else
            {
                var fileName = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
        }
        catch
        {
        }

        return fallbackName;
    }

    /// <summary>
    /// Resolve a versão efetiva do aplicativo em execução, priorizando o AssemblyInformationalVersion /
    /// AssemblyVersion quando builds Windows unpackaged deixam AppInfo.Current.VersionString em "1.0.0.1".
    /// </summary>
    public static string ResolveEffectiveCurrentVersion(
        System.Reflection.Assembly? assembly,
        string? appInfoVersion,
        string fallbackVersion = CurrentAppVersion)
    {
        string? infoVersion = null;
        string? asmVersion = null;
        if (assembly is not null)
        {
            try
            {
                var attrs = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0 && attrs[0] is System.Reflection.AssemblyInformationalVersionAttribute infoAttr)
                    infoVersion = infoAttr.InformationalVersion;

                asmVersion = assembly.GetName().Version?.ToString();
            }
            catch
            {
            }
        }

        return ResolveEffectiveCurrentVersion(infoVersion, asmVersion, appInfoVersion, fallbackVersion);
    }

    public static string ResolveEffectiveCurrentVersion(
        string? informationalVersion,
        string? assemblyVersion,
        string? appInfoVersion,
        string fallbackVersion = CurrentAppVersion)
    {
        var fromAssembly = new[] { informationalVersion, assemblyVersion }
            .Select(NormalizeVersion)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && Version.TryParse(v, out _));

        if (!string.IsNullOrWhiteSpace(fromAssembly))
            return fromAssembly;

        var fromAppInfo = NormalizeVersion(appInfoVersion);
        if (!string.IsNullOrWhiteSpace(fromAppInfo) && Version.TryParse(fromAppInfo, out _))
            return fromAppInfo;

        var normalizedFallback = NormalizeVersion(fallbackVersion);
        return string.IsNullOrWhiteSpace(normalizedFallback) ? CurrentAppVersion : normalizedFallback;
    }

    private static string EscapePowerShellSingleQuoted(string value) =>
        (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
}
