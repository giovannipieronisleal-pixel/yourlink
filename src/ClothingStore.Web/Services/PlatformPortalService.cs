using System.Globalization;
using ClothingStore.Application.Interfaces;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Web.Services;

/// <summary>
/// Dados dinâmicos exibidos na página inicial central da plataforma YouLink (/),
/// incluindo downloads oficiais do app (Windows/Android) e tabela viva de planos.
/// </summary>
public sealed record PlatformPortalSnapshot(
    string AppVersion,
    string ReleaseTitle,
    DateTimeOffset? PublishedAt,
    string ReleasePageUrl,
    string ReleasesAllUrl,
    string WindowsSetupAssetName,
    string WindowsSetupDownloadUrl,
    string? WindowsSetupSizeLabel,
    string WindowsZipAssetName,
    string WindowsZipDownloadUrl,
    string? WindowsZipSizeLabel,
    string AndroidApkAssetName,
    string AndroidApkDownloadUrl,
    string? AndroidApkSizeLabel,
    decimal BasicPlanMonthlyPrice,
    decimal IntermediaryPlanMonthlyPrice,
    decimal PremiumPlanMonthlyPrice,
    string PersistenceMode,
    bool HasPermanentCloudPersistence,
    bool LoadedLiveReleaseMetadata,
    string? SupportWhatsappNumber = null);

public sealed record PlatformReleaseDownloads(
    string Version,
    string ReleaseTitle,
    DateTimeOffset? PublishedAt,
    string ReleasePageUrl,
    string? WindowsSetupUrl,
    long? WindowsSetupSizeBytes,
    string? WindowsZipUrl,
    long? WindowsZipSizeBytes,
    string? AndroidApkUrl,
    long? AndroidApkSizeBytes);

public sealed class PlatformPortalService
{
    private static readonly object CacheLock = new();
    private static PlatformReleaseDownloads? _cachedDownloads;
    private static DateTimeOffset _cacheExpiresAtUtc = DateTimeOffset.MinValue;

    private readonly IClothingStoreRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly CloudAppDataMirror? _cloudMirror;

    public PlatformPortalService(
        IClothingStoreRepository repository,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        CloudAppDataMirror? cloudMirror = null)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _cloudMirror = cloudMirror;
    }

    /// <summary>
    /// Determina se a rota atual deve exibir o portal central da plataforma YouLink (/)
    /// em vez de uma vitrine de loja específica. Rotas /loja/{slug} sempre exibem a vitrine da loja.
    /// </summary>
    public async Task<bool> IsPortalHomeAsync(string? storeSlug, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(storeSlug))
            return false;

        var rawSetting = (_configuration["YOULINK_PORTAL_HOME"]
            ?? Environment.GetEnvironmentVariable("YOULINK_PORTAL_HOME")
            ?? string.Empty).Trim();

        if (rawSetting.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            rawSetting.Equals("1", StringComparison.Ordinal) ||
            rawSetting.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            rawSetting.Equals("sim", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (rawSetting.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            rawSetting.Equals("0", StringComparison.Ordinal) ||
            rawSetting.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            rawSetting.Equals("nao", StringComparison.OrdinalIgnoreCase) ||
            rawSetting.Equals("não", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var settings = await _repository.GetStoreSettingsAsync(ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        return products.Count == 0 &&
               string.Equals(settings.StoreName?.Trim(), "Minha Loja", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<PlatformPortalSnapshot> GetPortalSnapshotAsync(
        bool fetchLiveRelease = true,
        CancellationToken ct = default)
    {
        var settings = await _repository.GetStoreSettingsAsync(ct);
        var liveDownloads = fetchLiveRelease ? await TryGetCachedOrFetchDownloadsAsync(ct) : null;

        var baseDirect = $"https://github.com/{AppUpdateCatalog.GitHubOwner}/{AppUpdateCatalog.GitHubRepo}/releases/latest/download";
        var setupFallback = $"{baseDirect}/{AppUpdateCatalog.WindowsSetupAssetName}";
        var zipFallback = $"{baseDirect}/{AppUpdateCatalog.WindowsAssetName}";
        var apkFallback = $"{baseDirect}/{AppUpdateCatalog.AndroidAssetName}";

        var supportWhatsapp = WhatsAppLinkBuilder.SanitizeSupportPhone(
            _configuration["YOULINK_SUPPORT_WHATSAPP"]
            ?? Environment.GetEnvironmentVariable("YOULINK_SUPPORT_WHATSAPP")
            ?? settings.PlatformSupportWhatsapp);

        return new PlatformPortalSnapshot(
            AppVersion: liveDownloads?.Version ?? AppUpdateCatalog.CurrentAppVersion,
            ReleaseTitle: liveDownloads?.ReleaseTitle ?? $"YouLink v{AppUpdateCatalog.CurrentAppVersion}",
            PublishedAt: liveDownloads?.PublishedAt,
            ReleasePageUrl: liveDownloads?.ReleasePageUrl ?? AppUpdateCatalog.ReleasesPageUrl,
            ReleasesAllUrl: AppUpdateCatalog.ReleasesPageUrl,
            WindowsSetupAssetName: AppUpdateCatalog.WindowsSetupAssetName,
            WindowsSetupDownloadUrl: liveDownloads?.WindowsSetupUrl ?? setupFallback,
            WindowsSetupSizeLabel: FormatFileSize(liveDownloads?.WindowsSetupSizeBytes),
            WindowsZipAssetName: AppUpdateCatalog.WindowsAssetName,
            WindowsZipDownloadUrl: liveDownloads?.WindowsZipUrl ?? zipFallback,
            WindowsZipSizeLabel: FormatFileSize(liveDownloads?.WindowsZipSizeBytes),
            AndroidApkAssetName: AppUpdateCatalog.AndroidAssetName,
            AndroidApkDownloadUrl: liveDownloads?.AndroidApkUrl ?? apkFallback,
            AndroidApkSizeLabel: FormatFileSize(liveDownloads?.AndroidApkSizeBytes),
            BasicPlanMonthlyPrice: settings.BasicPlanMonthlyPrice,
            IntermediaryPlanMonthlyPrice: settings.IntermediaryPlanMonthlyPrice,
            PremiumPlanMonthlyPrice: settings.PremiumPlanMonthlyPrice,
            PersistenceMode: _cloudMirror?.ProviderName ?? "LocalDisk",
            HasPermanentCloudPersistence: _cloudMirror is not null,
            LoadedLiveReleaseMetadata: liveDownloads is not null,
            SupportWhatsappNumber: supportWhatsapp);
    }

    public static PlatformReleaseDownloads? ParseGitHubReleaseDownloads(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var setupRelease = AppUpdateCatalog.ParseGitHubReleaseJson(
            json,
            AppUpdateCatalog.CurrentAppVersion,
            isWindows: true,
            isAndroid: false,
            preferWindowsSetup: true);

        var zipAndAndroidRelease = AppUpdateCatalog.ParseGitHubReleaseJson(
            json,
            AppUpdateCatalog.CurrentAppVersion,
            isWindows: true,
            isAndroid: true,
            preferWindowsSetup: false);

        var primary = setupRelease ?? zipAndAndroidRelease;
        if (primary is null)
            return null;

        var setupUrl = setupRelease?.WindowsDownloadUrl;
        var setupSize = setupRelease?.WindowsAssetSizeBytes;
        if (!string.IsNullOrWhiteSpace(setupUrl) && !setupUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            setupUrl = null;
            setupSize = null;
        }

        var zipUrl = zipAndAndroidRelease?.WindowsDownloadUrl;
        var zipSize = zipAndAndroidRelease?.WindowsAssetSizeBytes;
        if (!string.IsNullOrWhiteSpace(zipUrl) && !zipUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            zipUrl = null;
            zipSize = null;
        }

        var apkUrl = zipAndAndroidRelease?.AndroidDownloadUrl;
        var apkSize = zipAndAndroidRelease?.AndroidAssetSizeBytes;
        if (!string.IsNullOrWhiteSpace(apkUrl) && !apkUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            apkUrl = null;
            apkSize = null;
        }

        return new PlatformReleaseDownloads(
            Version: primary.LatestVersion,
            ReleaseTitle: primary.ReleaseTitle,
            PublishedAt: primary.PublishedAt,
            ReleasePageUrl: primary.ReleasePageUrl,
            WindowsSetupUrl: setupUrl,
            WindowsSetupSizeBytes: setupSize,
            WindowsZipUrl: zipUrl,
            WindowsZipSizeBytes: zipSize,
            AndroidApkUrl: apkUrl,
            AndroidApkSizeBytes: apkSize);
    }

    public static string? FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value <= 0)
            return null;

        double value = bytes.Value;
        if (value < 1024 * 1024)
            return string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{value / 1024d:F0} KB");

        return string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{value / (1024d * 1024d):F1} MB");
    }

    private async Task<PlatformReleaseDownloads?> TryGetCachedOrFetchDownloadsAsync(CancellationToken ct)
    {
        lock (CacheLock)
        {
            if (_cachedDownloads is not null && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
                return _cachedDownloads;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(4);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"YouLink-Portal/{AppUpdateCatalog.CurrentAppVersion}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var releaseApiUrl = (_configuration["YOULINK_GITHUB_RELEASE_API_URL"]
                ?? Environment.GetEnvironmentVariable("YOULINK_GITHUB_RELEASE_API_URL")
                ?? AppUpdateCatalog.GitHubLatestReleaseApiUrl).Trim();

            using var response = await client.GetAsync(releaseApiUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                lock (CacheLock)
                {
                    if (_cachedDownloads is not null)
                        return _cachedDownloads;
                }
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var parsed = ParseGitHubReleaseDownloads(json);
            if (parsed is not null)
            {
                lock (CacheLock)
                {
                    _cachedDownloads = parsed;
                    _cacheExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                }
            }

            return parsed;
        }
        catch
        {
            lock (CacheLock)
            {
                return _cachedDownloads;
            }
        }
    }
}
