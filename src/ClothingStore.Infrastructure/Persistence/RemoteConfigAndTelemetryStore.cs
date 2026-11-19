using System.Text;
using System.Text.Json;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Armazena configurações remotas dinâmicas (compatíveis com Firebase Remote Config)
/// e buffer circular sanitizado de relatórios de falhas dos clientes (estilo Crashlytics),
/// sincronizados automaticamente via <see cref="CloudAppDataMirrorRegistry"/>.
/// </summary>
public sealed class RemoteConfigAndTelemetryStore
{
    public const string RemoteConfigFileName = "remote_config.json";
    public const string TelemetryFileName = "client_telemetry.json";
    private const int MaxStoredErrors = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configPath;
    private readonly string _telemetryPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RemoteConfigAndTelemetryStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRoot = Path.GetFullPath(rootDirectory);
        _configPath = Path.Combine(fullRoot, RemoteConfigFileName);
        _telemetryPath = Path.Combine(fullRoot, TelemetryFileName);
    }

    public async Task<RemoteFeatureConfigDto> GetRemoteConfigAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var stored = ReadConfigUnsafe();
            return ApplyEnvironmentOverrides(stored);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RemoteFeatureConfigDto> SaveRemoteConfigAsync(RemoteFeatureConfigDto config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sanitized = config with
        {
            GlobalAnnouncementBanner = TrimOrNull(config.GlobalAnnouncementBanner, 320),
            GlobalAnnouncementKind = NormalizeKind(config.GlobalAnnouncementKind),
            VipPromoMessage = TrimOrNull(config.VipPromoMessage, 280),
            MinRecommendedAppVersion = TrimOrNull(config.MinRecommendedAppVersion, 32) ?? AppUpdateCatalog.CurrentAppVersion,
            EdgeProxyBaseUrl = TrimOrNull(config.EdgeProxyBaseUrl, 240),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _lock.WaitAsync(ct);
        try
        {
            WriteAtomicallyUnsafe(_configPath, sanitized);
            return ApplyEnvironmentOverrides(sanitized);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ClientCrashReportEntryDto> RecordClientErrorAsync(ClientCrashReportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entry = new ClientCrashReportEntryDto(
            Id: Guid.NewGuid(),
            ReportedAt: DateTimeOffset.UtcNow,
            AppName: TrimOrNull(request.AppName, 60) ?? "YouLink",
            AppVersion: TrimOrNull(request.AppVersion, 32) ?? AppUpdateCatalog.CurrentAppVersion,
            Platform: TrimOrNull(request.Platform, 40) ?? "Unknown",
            Context: TrimOrNull(request.Context, 120) ?? "Unhandled",
            ErrorType: TrimOrNull(request.ErrorType, 120) ?? "Exception",
            ErrorMessage: SanitizeErrorText(request.ErrorMessage, 500),
            StackTrace: SanitizeErrorText(request.StackTrace, 1500),
            AccountHint: TrimOrNull(request.AccountHint, 80));

        await _lock.WaitAsync(ct);
        try
        {
            var list = ReadTelemetryUnsafe();
            list.Insert(0, entry);
            if (list.Count > MaxStoredErrors)
                list.RemoveRange(MaxStoredErrors, list.Count - MaxStoredErrors);

            WriteAtomicallyUnsafe(_telemetryPath, list);
            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ClientCrashReportEntryDto>> GetRecentErrorsAsync(int maxCount = 50, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = ReadTelemetryUnsafe();
            var limit = Math.Clamp(maxCount, 1, MaxStoredErrors);
            return list.Take(limit).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private RemoteFeatureConfigDto ReadConfigUnsafe()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var raw = File.ReadAllText(_configPath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<RemoteFeatureConfigDto>(raw, JsonOptions);
                if (parsed is not null)
                    return parsed;
            }
            catch
            {
            }
        }

        return new RemoteFeatureConfigDto(
            AiStudioEnabled: true,
            TurnstileCheckoutEnabled: true,
            CrashTelemetryEnabled: true,
            GlobalAnnouncementBanner: null,
            GlobalAnnouncementKind: "info",
            VipPromoMessage: "Libere vendas diretas por Pix, Estúdio Visual e Estúdio IA Gemini com os planos YouLink.",
            MinRecommendedAppVersion: AppUpdateCatalog.CurrentAppVersion,
            EdgeProxyBaseUrl: null,
            UpdatedAt: null);
    }

    private static RemoteFeatureConfigDto ApplyEnvironmentOverrides(RemoteFeatureConfigDto baseCfg)
    {
        var envBanner = TrimOrNull(Environment.GetEnvironmentVariable("YOULINK_GLOBAL_BANNER"), 320);
        var envEdge = TrimOrNull(Environment.GetEnvironmentVariable("YOULINK_EDGE_PROXY_URL"), 240);
        var envAi = Environment.GetEnvironmentVariable("YOULINK_AI_STUDIO_ENABLED");

        return baseCfg with
        {
            GlobalAnnouncementBanner = envBanner ?? baseCfg.GlobalAnnouncementBanner,
            EdgeProxyBaseUrl = envEdge ?? baseCfg.EdgeProxyBaseUrl,
            AiStudioEnabled = bool.TryParse(envAi, out var parsedAi) ? parsedAi : baseCfg.AiStudioEnabled
        };
    }

    private List<ClientCrashReportEntryDto> ReadTelemetryUnsafe()
    {
        if (!File.Exists(_telemetryPath))
            return [];

        try
        {
            var raw = File.ReadAllText(_telemetryPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<ClientCrashReportEntryDto>>(raw, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteAtomicallyUnsafe<T>(string path, T payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
            CloudAppDataMirrorRegistry.TryNotifySaved(path, json);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
            }
        }
    }

    private static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "warning" => "warning",
        "success" => "success",
        _ => "info"
    };

    private static string? TrimOrNull(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var clean = value.Trim();
        return clean.Length <= maxLen ? clean : clean[..maxLen];
    }

    private static string SanitizeErrorText(string? value, int maxLen)
    {
        var trimmed = TrimOrNull(value, maxLen);
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        // Remove possíveis segredos/tokens acidentais antes de persistir telemetria
        var sanitized = trimmed
            .Replace("Bearer ", "Bearer [REDACTED] ", StringComparison.OrdinalIgnoreCase)
            .Replace("x-goog-api-key", "[REDACTED]", StringComparison.OrdinalIgnoreCase)
            .Replace("X-YouLink-Sync-Key", "[REDACTED]", StringComparison.OrdinalIgnoreCase);

        return sanitized.Length <= maxLen ? sanitized : sanitized[..maxLen];
    }
}
