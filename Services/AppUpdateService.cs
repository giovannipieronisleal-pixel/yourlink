using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Verifica atualizações oficiais no GitHub Releases / API v1, exibe o popup no aplicativo
/// e executa o download e instalação direta pelo Windows ou Android.
/// </summary>
public sealed class AppUpdateService
{
    private const string IgnoredVersionPreferenceKey = "youlink_ignored_update_version_v1";
    private const string AppliedCloudUpdateVersionKey = "youlink_applied_cloud_update_version_v1";
    private const long MinimumValidPackageBytes = 100 * 1024;
    private static readonly TimeSpan AutoCheckCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResumeCheckCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeriodicMonitorInterval = TimeSpan.FromMinutes(3);

    private static readonly HttpClient DownloadHttpClient = CreateUpdateHttpClient();
    private static readonly HttpClient RedirectProbeHttpClient = CreateRedirectProbeHttpClient();
    private readonly MauiAdminGateway _gateway;
    private readonly GoogleCloudSyncService? _cloudSync;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private DateTimeOffset _lastCheckedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;
    private bool _sessionPopupShown;
    private string? _lastAutoPromptedVersion;
    private int _monitorStarted;

    public event Action? StateChanged;

    public AppUpdateReleaseInfo? AvailableRelease { get; private set; }
    public bool IsPopupOpen { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsApplyingUpdate { get; private set; }
    public int DownloadProgressPercent { get; private set; }
    public long DownloadedBytes { get; private set; }
    public long TotalBytes { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? DownloadedPackagePath { get; private set; }
    public string? PublicDownloadsApkPath { get; private set; }
    public bool WaitingAndroidInstallPermission { get; private set; }
    public bool AndroidSignatureConflictDetected { get; private set; }
    public bool CanApplyDirectlyInApp => OperatingSystem.IsWindows() || OperatingSystem.IsAndroid();

    public string CurrentVersion => ResolveCurrentVersion();

    public AppUpdateService(MauiAdminGateway gateway, GoogleCloudSyncService? cloudSync = null)
    {
        _gateway = gateway;
        _cloudSync = cloudSync;
        CleanupStaleUpdateArtifactsSafe();
    }

    internal static string GetUpdaterCacheRoot()
        => OperatingSystem.IsAndroid()
            ? Path.Combine(FileSystem.CacheDirectory, "updates")
            : Path.Combine(Path.GetTempPath(), "YouLinkUpdater");

    private void CleanupStaleUpdateArtifactsSafe(string? latestKnownVersion = null)
    {
        try
        {
            AppUpdateCatalog.CleanupStaleUpdateArtifacts(
                updaterCacheRootDir: GetUpdaterCacheRoot(),
                currentVersion: CurrentVersion,
                latestKnownVersion: latestKnownVersion,
                activePackagePath: DownloadedPackagePath,
                preserveActivePackage: IsDownloading || IsApplyingUpdate || WaitingAndroidInstallPermission,
                maxPendingPackageAge: TimeSpan.FromMinutes(15));

            if (!string.IsNullOrWhiteSpace(DownloadedPackagePath) && !File.Exists(DownloadedPackagePath))
            {
                DownloadedPackagePath = null;
                WaitingAndroidInstallPermission = false;
            }
        }
        catch
        {
        }
    }

    private void PurgeUpdaterCacheSafe()
    {
        try
        {
            AppUpdateCatalog.PurgeUpdaterCache(GetUpdaterCacheRoot());
            DownloadedPackagePath = null;
            WaitingAndroidInstallPermission = false;
        }
        catch
        {
        }
    }

    public void StartBackgroundMonitor()
    {
        if (Interlocked.CompareExchange(ref _monitorStarted, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(PeriodicMonitorInterval);
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    if (IsChecking || IsDownloading || IsApplyingUpdate || IsPopupOpen)
                        continue;

                    await CheckForUpdatesAsync(manualTrigger: false, forceBypassCache: true);
                }
                catch
                {
                }
            }
        });
    }

    public async Task CheckForUpdatesOnResumeAsync(CancellationToken ct = default)
    {
        if (IsChecking || IsDownloading || IsApplyingUpdate || IsPopupOpen)
            return;

        if (AvailableRelease is not null && DateTimeOffset.UtcNow - _lastCheckedAt < ResumeCheckCooldown)
            return;

        await CheckForUpdatesAsync(manualTrigger: false, forceBypassCache: true, ct: ct);
    }

    public Task<AppUpdateReleaseInfo?> CheckForUpdatesAsync(
        bool manualTrigger = false,
        CancellationToken ct = default)
        => CheckForUpdatesAsync(manualTrigger, forceBypassCache: false, ct);

    public async Task<AppUpdateReleaseInfo?> CheckForUpdatesAsync(
        bool manualTrigger,
        bool forceBypassCache,
        CancellationToken ct = default)
    {
        if (!manualTrigger &&
            !forceBypassCache &&
            DateTimeOffset.UtcNow - _lastAttemptAt < AutoCheckCooldown)
        {
            if (AvailableRelease is not null)
            {
                var isUnprompted = !string.Equals(_lastAutoPromptedVersion, AvailableRelease.LatestVersion, StringComparison.OrdinalIgnoreCase);
                if (ShouldAutoPrompt(AvailableRelease) && (!_sessionPopupShown || isUnprompted))
                {
                    _lastAutoPromptedVersion = AvailableRelease.LatestVersion;
                    _sessionPopupShown = true;
                    IsPopupOpen = true;
                    NotifyChanged();
                }
            }
            return AvailableRelease;
        }

        await _checkLock.WaitAsync(ct);
        try
        {
            if (!manualTrigger &&
                !forceBypassCache &&
                DateTimeOffset.UtcNow - _lastAttemptAt < AutoCheckCooldown)
            {
                return AvailableRelease;
            }

            _lastAttemptAt = DateTimeOffset.UtcNow;

            IsChecking = true;
            ErrorMessage = null;
            if (manualTrigger)
            {
                StatusMessage = "Procurando nova versão do YouLink...";
            }
            NotifyChanged();

            var currentVersion = ResolveCurrentVersion();
            var isWindows = OperatingSystem.IsWindows();
            var isAndroid = OperatingSystem.IsAndroid();
            var preferWindowsSetup = isWindows && AppUpdateCatalog.IsWindowsSetupInstallation(AppContext.BaseDirectory);

            AppUpdateReleaseInfo? githubRelease = null;
            string? githubError = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                using var req = new HttpRequestMessage(HttpMethod.Get, AppUpdateCatalog.GitHubLatestReleaseApiUrl);
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var resp = await DownloadHttpClient.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    githubRelease = AppUpdateCatalog.ParseGitHubReleaseJson(
                        json,
                        currentVersion,
                        isWindows,
                        isAndroid,
                        preferWindowsSetup);
                }
                else
                {
                    githubError = $"GitHub retornou HTTP {(int)resp.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                githubError = ex.Message;
            }

            if (githubRelease is null)
            {
                try
                {
                    using var redirectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    redirectCts.CancelAfter(TimeSpan.FromSeconds(12));
                    githubRelease = await TryResolveReleaseViaGitHubRedirectAsync(
                        currentVersion,
                        isWindows,
                        isAndroid,
                        preferWindowsSetup,
                        redirectCts.Token);
                }
                catch (Exception ex)
                {
                    githubError ??= ex.Message;
                }
            }

            ApiCompatibilityStatusDto? apiCompat = null;
            try
            {
                apiCompat = await _gateway.RefreshCompatibilityAsync(ct);
            }
            catch
            {
            }

            var resolved = AppUpdateCatalog.MergeWithApiCompatibility(
                githubRelease,
                apiCompat,
                currentVersion,
                isWindows,
                isAndroid,
                preferWindowsSetup);

            if (resolved is not null)
            {
                AvailableRelease = resolved;
                _lastCheckedAt = DateTimeOffset.UtcNow;

                if (resolved.IsUpdateAvailable)
                {
                    StatusMessage = $"Nova versão v{resolved.LatestVersion} pronta para atualização.";
                    var isUnprompted = !string.Equals(_lastAutoPromptedVersion, resolved.LatestVersion, StringComparison.OrdinalIgnoreCase);
                    if (manualTrigger || (ShouldAutoPrompt(resolved) && (!_sessionPopupShown || isUnprompted)))
                    {
                        _lastAutoPromptedVersion = resolved.LatestVersion;
                        _sessionPopupShown = true;
                        IsPopupOpen = true;
                    }
                }
                else if (manualTrigger)
                {
                    StatusMessage = $"✓ Você já está usando a versão mais recente (v{currentVersion}).";
                }
            }
            else if (manualTrigger)
            {
                ErrorMessage = $"Não foi possível verificar atualizações agora ({githubError ?? "sem resposta da internet"}).";
            }

            CleanupStaleUpdateArtifactsSafe(resolved?.LatestVersion);
            return AvailableRelease;
        }
        finally
        {
            IsChecking = false;
            NotifyChanged();
            _checkLock.Release();
        }
    }

    public void OpenPopup()
    {
        ErrorMessage = null;
        IsPopupOpen = true;
        NotifyChanged();
    }

    public void DismissPopupForNow()
    {
        if (IsDownloading || IsApplyingUpdate)
            return;

        IsPopupOpen = false;
        NotifyChanged();
    }

    public void IgnoreCurrentAvailableVersion()
    {
        if (IsDownloading || IsApplyingUpdate)
            return;

        if (AvailableRelease is not null && !string.IsNullOrWhiteSpace(AvailableRelease.LatestVersion))
        {
            try
            {
                Preferences.Default.Set(IgnoredVersionPreferenceKey, AvailableRelease.LatestVersion);
            }
            catch
            {
            }
        }

        PurgeUpdaterCacheSafe();
        IsPopupOpen = false;
        NotifyChanged();
    }

    public async Task DownloadAndInstallAsync(CancellationToken ct = default)
    {
        if (IsDownloading || IsApplyingUpdate)
            return;

        var release = AvailableRelease;
        if (release is null)
        {
            await CheckForUpdatesAsync(manualTrigger: true, ct);
            release = AvailableRelease;
        }

        if (release is null || !release.IsUpdateAvailable)
        {
            StatusMessage = $"✓ Você já está na versão mais recente (v{CurrentVersion}).";
            NotifyChanged();
            return;
        }

        if (OperatingSystem.IsAndroid() &&
            WaitingAndroidInstallPermission &&
            !string.IsNullOrWhiteSpace(DownloadedPackagePath) &&
            File.Exists(DownloadedPackagePath))
        {
            await LaunchAndroidInstallerAsync(DownloadedPackagePath, release.LatestVersion);
            return;
        }

        if (string.IsNullOrWhiteSpace(release.SelectedDownloadUrl) ||
            string.IsNullOrWhiteSpace(release.SelectedAssetName))
        {
            await OpenReleaseFallbackAsync();
            return;
        }

        IsDownloading = true;
        IsApplyingUpdate = false;
        WaitingAndroidInstallPermission = false;
        ErrorMessage = null;
        DownloadProgressPercent = 0;
        DownloadedBytes = 0;
        TotalBytes = release.SelectedAssetSizeBytes ?? 0;
        StatusMessage = $"Baixando {release.SelectedAssetName} (v{release.LatestVersion})...";
        NotifyChanged();

        try
        {
            var updaterRoot = GetUpdaterCacheRoot();
            AppUpdateCatalog.PurgeUpdaterCache(updaterRoot);

            var safeVersionFolder = AppUpdateCatalog.NormalizeVersion(release.LatestVersion);
            if (string.IsNullOrWhiteSpace(safeVersionFolder))
                safeVersionFolder = "latest";

            var updateDir = Path.Combine(updaterRoot, safeVersionFolder);

            Directory.CreateDirectory(updateDir);
            var targetFile = Path.Combine(updateDir, release.SelectedAssetName);

            await DownloadFileWithProgressAsync(release.SelectedDownloadUrl, targetFile, ct);
            var expectedSha256 = AppUpdateCatalog.NormalizeSha256Hex(release.SelectedAssetSha256)
                                 ?? await TryFetchCompanionSha256Async(release.SelectedDownloadUrl, release.SelectedAssetName, ct);
            try
            {
                ValidateDownloadedArchive(targetFile);
                AppUpdateCatalog.VerifyDownloadedPackageSha256(targetFile, expectedSha256);
            }
            catch
            {
                try
                {
                    if (File.Exists(targetFile))
                        File.Delete(targetFile);
                }
                catch
                {
                }

                throw;
            }

            DownloadedPackagePath = targetFile;

            IsDownloading = false;
            IsApplyingUpdate = true;
            DownloadProgressPercent = 100;
            NotifyChanged();

            if (OperatingSystem.IsWindows())
            {
                await ApplyWindowsUpdateAsync(targetFile, updateDir, release.LatestVersion, expectedSha256, ct);
            }
            else if (OperatingSystem.IsAndroid())
            {
                await LaunchAndroidInstallerAsync(targetFile, release.LatestVersion);
            }
            else
            {
                await OpenReleaseFallbackAsync();
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Download da atualização cancelado.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Falha ao atualizar pelo app: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            IsApplyingUpdate = false;
            NotifyChanged();
        }
    }

    public async Task ApplyCloudHotUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            Preferences.Default.Remove(AppliedCloudUpdateVersionKey);
        }
        catch
        {
        }

        if (_cloudSync is not null)
        {
            try
            {
                await _cloudSync.EnsureCloudMirrorPopulatedAsync();
            }
            catch
            {
            }
        }

        await DownloadAndInstallAsync(ct);
    }

    public async Task ReplaceConflictingAndroidPackageAsync()
    {
        var targetVersion = AvailableRelease?.LatestVersion ?? CurrentVersion;
        ErrorMessage = null;
        StatusMessage = "Sincronizando dados na nuvem antes de substituir o pacote antigo...";
        NotifyChanged();

        if (_cloudSync is not null)
        {
            try
            {
                await _cloudSync.EnsureCloudMirrorPopulatedAsync();
            }
            catch
            {
            }
        }

#if ANDROID
        try
        {
            if (!string.IsNullOrWhiteSpace(DownloadedPackagePath) && File.Exists(DownloadedPackagePath))
            {
                PublicDownloadsApkPath = TrySaveApkToPublicDownloads(DownloadedPackagePath, targetVersion);
            }

            var packageUri = global::Android.Net.Uri.Parse($"package:{Platform.AppContext.PackageName}");
            var uninstallIntent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionDelete, packageUri);
            uninstallIntent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(uninstallIntent);

            StatusMessage = !string.IsNullOrWhiteSpace(PublicDownloadsApkPath)
                ? $"Confirme a desinstalação da versão antiga e depois abra o arquivo salvo em Downloads ({Path.GetFileName(PublicDownloadsApkPath)}) para instalar a v{targetVersion}."
                : $"Confirme a desinstalação da versão antiga e instale o novo APK v{targetVersion}.";
            NotifyChanged();
            return;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Não foi possível abrir a remoção automática: {ex.Message}";
            NotifyChanged();
        }
#endif
        await OpenReleaseFallbackAsync();
    }

    public async Task OpenReleaseFallbackAsync()
    {
        var url = AvailableRelease?.SelectedDownloadUrl ?? AvailableRelease?.ReleasePageUrl ?? AppUpdateCatalog.ReleasesPageUrl;
        try
        {
            await Launcher.Default.OpenAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Não foi possível abrir o navegador: {ex.Message}";
            NotifyChanged();
        }
    }

    private async Task DownloadFileWithProgressAsync(string downloadUrl, string destinationPath, CancellationToken ct)
    {
        using var response = await DownloadHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? TotalBytes;
        if (total > 0)
            TotalBytes = total;

        var tempFile = destinationPath + ".part";
        try
        {
            await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                var lastReportedPct = -1;
                var lastNotifiedAt = DateTimeOffset.UtcNow;

                while (true)
                {
                    var read = await remoteStream.ReadAsync(buffer, ct);
                    if (read <= 0)
                        break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    DownloadedBytes = readTotal;

                    var pct = TotalBytes > 0
                        ? Math.Clamp((int)Math.Round(readTotal * 100d / TotalBytes), 1, 99)
                        : 0;

                    var now = DateTimeOffset.UtcNow;
                    if (pct != lastReportedPct || (now - lastNotifiedAt).TotalMilliseconds >= 250)
                    {
                        lastReportedPct = pct;
                        lastNotifiedAt = now;
                        DownloadProgressPercent = pct;
                        StatusMessage = TotalBytes > 0
                            ? $"Baixando atualização... {pct}% ({AppUpdateCatalog.FormatFileSize(readTotal)} de {AppUpdateCatalog.FormatFileSize(TotalBytes)})"
                            : $"Baixando atualização... {AppUpdateCatalog.FormatFileSize(readTotal)}";
                        NotifyChanged();
                    }
                }
            }

            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            File.Move(tempFile, destinationPath);
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
            }

            throw;
        }
    }

    private static void ValidateDownloadedArchive(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length < MinimumValidPackageBytes)
        {
            throw new InvalidOperationException("O arquivo baixado está incompleto ou inválido.");
        }

        Span<byte> header = stackalloc byte[2];
        using var fs = File.OpenRead(filePath);
        if (fs.Read(header) < 2)
        {
            throw new InvalidOperationException("O arquivo baixado está vazio ou corrompido.");
        }

        if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new InvalidOperationException("O arquivo baixado não é um instalador Windows válido (.exe).");
            }
            return;
        }

        if (header[0] != (byte)'P' || header[1] != (byte)'K')
        {
            throw new InvalidOperationException("O pacote baixado não parece ser um instalador válido (.zip/.apk).");
        }
    }

    private async Task<string?> TryFetchCompanionSha256Async(string downloadUrl, string assetName, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await DownloadHttpClient.GetAsync(downloadUrl + ".sha256", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                return AppUpdateCatalog.ParseSha256FromChecksumText(text, assetName);
            }
        }
        catch
        {
        }
        return null;
    }

    private async Task ApplyWindowsUpdateAsync(
        string packagePath,
        string updateDir,
        string targetVersion,
        string? expectedSha256,
        CancellationToken ct)
    {
        var currentAppDir = AppContext.BaseDirectory;
        if (AppUpdateCatalog.IsDevelopmentBuildDirectory(currentAppDir))
        {
            StatusMessage = $"✓ Pacote v{targetVersion} baixado e validado em: {packagePath} (pasta de desenvolvimento preservada).";
            TryOpenWindowsFolder(updateDir);
            NotifyChanged();
            return;
        }

        var scriptPath = Path.Combine(updateDir, "apply-youlink-update.ps1");
        var logPath = Path.Combine(updateDir, "update.log");
        string script;

        if (packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Preparando instalador da nova versão...";
            NotifyChanged();

            var parentDir = Directory.GetParent(currentAppDir.TrimEnd('\\', '/'))?.FullName;
            var launcherPath = parentDir is not null && File.Exists(Path.Combine(parentDir, AppUpdateCatalog.WindowsLauncherName))
                ? Path.Combine(parentDir, AppUpdateCatalog.WindowsLauncherName)
                : Path.Combine(currentAppDir, AppUpdateCatalog.WindowsExecutableName);

            script = AppUpdateCatalog.BuildWindowsInstallerUpdateScript(
                Environment.ProcessId,
                packagePath,
                launcherPath,
                logPath,
                updaterCacheRootDir: GetUpdaterCacheRoot(),
                expectedSha256: expectedSha256);
        }
        else
        {
            StatusMessage = "Extraindo nova versão...";
            NotifyChanged();

            var stagingDir = Path.Combine(updateDir, "staging");
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }

            ZipFile.ExtractToDirectory(packagePath, stagingDir, overwriteFiles: true);
            var sourceAppDir = AppUpdateCatalog.ResolveExtractedAppRoot(stagingDir);
            if (string.IsNullOrWhiteSpace(sourceAppDir))
            {
                throw new InvalidOperationException("Executável do YouLink não encontrado dentro do pacote Windows.");
            }

            script = AppUpdateCatalog.BuildWindowsApplyUpdateScript(
                Environment.ProcessId,
                sourceAppDir,
                currentAppDir,
                AppUpdateCatalog.WindowsExecutableName,
                logPath,
                updaterCacheRootDir: GetUpdaterCacheRoot());
        }

        await File.WriteAllTextAsync(scriptPath, script, ct);

        var needsElevation = !TryProbeDirectoryWritable(currentAppDir);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (needsElevation)
        {
            psi.Verb = "runas";
        }

        LocalWebServerLauncher.Stop();
        Process.Start(psi);
        StatusMessage = $"Aplicando atualização v{targetVersion} e reiniciando o YouLink...";
        NotifyChanged();

        await Task.Delay(500, ct);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Application.Current?.Quit();
            }
            catch
            {
                Environment.Exit(0);
            }
        });

        await Task.Delay(1200, CancellationToken.None);
        Environment.Exit(0);
    }

    private async Task LaunchAndroidInstallerAsync(string apkPath, string targetVersion)
    {
        AndroidSignatureConflictDetected = false;
#if ANDROID
        PublicDownloadsApkPath = TrySaveApkToPublicDownloads(apkPath, targetVersion);

        if (HasAndroidSignatureConflict(apkPath))
        {
            AndroidSignatureConflictDetected = true;
            var savedNotice = !string.IsNullOrWhiteSpace(PublicDownloadsApkPath)
                ? $" O instalador também foi salvo na sua pasta Downloads ({Path.GetFileName(PublicDownloadsApkPath)})."
                : string.Empty;
            ErrorMessage = $"O Android detectou diferença de assinatura entre a versão instalada e o novo APK.{savedNotice} Toque em '🔄 Substituir Pacote Antigo (1 Toque)' abaixo para remover a versão antiga e instalar o novo APK.";
            StatusMessage = $"Toque em '🔄 Substituir Pacote Antigo' abaixo para concluir a instalação da v{targetVersion}:";
            NotifyChanged();
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26) &&
            !(Platform.AppContext.PackageManager?.CanRequestPackageInstalls() ?? true))
        {
            WaitingAndroidInstallPermission = true;
            StatusMessage = "Permita 'Instalar apps desconhecidos' para o YouLink nas Configurações do Android e depois toque em 'Instalar APK Baixado'.";
            NotifyChanged();

            var packageUri = global::Android.Net.Uri.Parse($"package:{Platform.AppContext.PackageName}");
            var permissionIntent = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionManageUnknownAppSources,
                packageUri);
            permissionIntent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(permissionIntent);
            return;
        }
#endif

        WaitingAndroidInstallPermission = false;
        StatusMessage = $"Abrindo instalador do Android para o YouLink v{targetVersion}...";
        NotifyChanged();

        var opened = await Launcher.Default.OpenAsync(new OpenFileRequest
        {
            Title = $"Instalar atualização YouLink v{targetVersion}",
            File = new ReadOnlyFile(apkPath, "application/vnd.android.package-archive")
        });

        if (!opened)
        {
            ErrorMessage = "O Android não conseguiu abrir o pacote automaticamente. Toque em '🔄 Substituir Pacote Antigo' ou '🌐 Abrir Página do GitHub' para instalar o APK.";
        }
        else
        {
            StatusMessage = $"✓ Instalador do Android aberto! Confirme a instalação na tela do Android para atualizar para a v{targetVersion}.";
        }
    }

#if ANDROID
    private static bool HasAndroidSignatureConflict(string apkPath)
    {
        try
        {
            var pm = Platform.AppContext.PackageManager;
            var pkgName = Platform.AppContext.PackageName;
            if (pm is null || string.IsNullOrWhiteSpace(pkgName) || !File.Exists(apkPath))
                return false;

            byte[]? installedCert = null;
            byte[]? archiveCert = null;

            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                var installedInfo = pm.GetPackageInfo(pkgName, global::Android.Content.PM.PackageInfoFlags.SigningCertificates);
                installedCert = installedInfo?.SigningInfo?.GetApkContentsSigners()?.FirstOrDefault()?.ToByteArray();

                var archiveInfo = pm.GetPackageArchiveInfo(apkPath, global::Android.Content.PM.PackageInfoFlags.SigningCertificates);
                archiveCert = archiveInfo?.SigningInfo?.GetApkContentsSigners()?.FirstOrDefault()?.ToByteArray();
            }
            else
            {
#pragma warning disable CA1422
                var installedInfo = pm.GetPackageInfo(pkgName, global::Android.Content.PM.PackageInfoFlags.Signatures);
                installedCert = installedInfo?.Signatures?.FirstOrDefault()?.ToByteArray();

                var archiveInfo = pm.GetPackageArchiveInfo(apkPath, global::Android.Content.PM.PackageInfoFlags.Signatures);
                archiveCert = archiveInfo?.Signatures?.FirstOrDefault()?.ToByteArray();
#pragma warning restore CA1422
            }

            if (installedCert is { Length: > 0 } && archiveCert is { Length: > 0 })
            {
                return !installedCert.AsSpan().SequenceEqual(archiveCert);
            }
        }
        catch
        {
        }

        return false;
    }

    private static string? TrySaveApkToPublicDownloads(string sourceApkPath, string targetVersion)
    {
        try
        {
            var safeVer = AppUpdateCatalog.NormalizeVersion(targetVersion);
            var fileName = string.IsNullOrWhiteSpace(safeVer)
                ? "YouLink-Android.apk"
                : $"YouLink-Android-v{safeVer}.apk";

            var downloadsDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
                global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;

            if (!string.IsNullOrWhiteSpace(downloadsDir))
            {
                Directory.CreateDirectory(downloadsDir);
                var destPath = Path.Combine(downloadsDir, fileName);
                File.Copy(sourceApkPath, destPath, overwrite: true);
                return destPath;
            }
        }
        catch
        {
        }

        return null;
    }
#endif

    private bool ShouldAutoPrompt(AppUpdateReleaseInfo release)
    {
        if (!release.IsUpdateAvailable)
            return false;

        try
        {
            var ignoredVersion = Preferences.Default.Get(IgnoredVersionPreferenceKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(ignoredVersion) &&
                string.Equals(ignoredVersion.Trim(), release.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
        }

        return true;
    }

    private static string ResolveCurrentVersion()
    {
        string? appInfoVersion = null;
        try
        {
            appInfoVersion = AppInfo.Current.VersionString;
        }
        catch
        {
        }

        var binaryVersion = AppUpdateCatalog.ResolveEffectiveCurrentVersion(
            typeof(AppUpdateService).Assembly,
            appInfoVersion,
            AppUpdateCatalog.CurrentAppVersion);

        try
        {
            Preferences.Default.Remove(AppliedCloudUpdateVersionKey);
        }
        catch
        {
        }

        return binaryVersion;
    }

    private static bool TryProbeDirectoryWritable(string dirPath)
    {
        try
        {
            var probe = Path.Combine(dirPath, $".youlink-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryOpenWindowsFolder(string dirPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(dirPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dirPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }

    private static async Task<AppUpdateReleaseInfo?> TryResolveReleaseViaGitHubRedirectAsync(
        string currentVersion,
        bool isWindows,
        bool isAndroid,
        bool preferWindowsSetup,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, AppUpdateCatalog.ReleasesPageUrl);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var resp = await RedirectProbeHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        string? location = resp.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location))
        {
            location = resp.RequestMessage?.RequestUri?.ToString();
        }
        else if (!location.StartsWith("http", StringComparison.OrdinalIgnoreCase) && resp.RequestMessage?.RequestUri is not null)
        {
            location = new Uri(resp.RequestMessage.RequestUri, location).ToString();
        }

        return AppUpdateCatalog.ParseGitHubRedirectLocation(
            location,
            currentVersion,
            isWindows,
            isAndroid,
            preferWindowsSetup);
    }

    private static HttpClient CreateRedirectProbeHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YouLink-App", AppUpdateCatalog.CurrentAppVersion));
        return client;
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YouLink-App", AppUpdateCatalog.CurrentAppVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private void NotifyChanged() => StateChanged?.Invoke();
}
