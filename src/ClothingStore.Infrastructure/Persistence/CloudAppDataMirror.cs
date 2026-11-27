using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Abstração de backend externo gratuito para persistência durável dos estados JSON em App_Data
/// (PostgreSQL via Neon/Supabase/Render ou bucket privado no Supabase Storage).
/// </summary>
public interface ICloudStateBackend
{
    string ProviderName { get; }

    Task<IReadOnlyDictionary<string, string>> LoadAllSnapshotsAsync(CancellationToken ct = default);

    Task UpsertSnapshotAsync(string stateKey, string payload, CancellationToken ct = default);

    Task DeleteSnapshotAsync(string stateKey, CancellationToken ct = default);
}

/// <summary>
/// Backend PostgreSQL gratuito compatível com Neon.tech, Supabase Postgres ou qualquer conexão DATABASE_URL.
/// Cria automaticamente a tabela `youlink_state_snapshots` na inicialização sem necessidade de SQL manual.
/// </summary>
public sealed class PostgresCloudStateBackend : ICloudStateBackend
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _tableEnsured;

    public string ProviderName => "PostgreSQL";

    public PostgresCloudStateBackend(string rawConnectionString)
    {
        if (string.IsNullOrWhiteSpace(rawConnectionString))
            throw new ArgumentException("Connection string PostgreSQL vazia.", nameof(rawConnectionString));

        _connectionString = NormalizePostgresConnectionString(rawConnectionString);
    }

    public static string NormalizePostgresConnectionString(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var uri = new Uri(trimmed);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Timeout = 10,
            CommandTimeout = 15
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var sep = uri.UserInfo.IndexOf(':');
            if (sep >= 0)
            {
                builder.Username = Uri.UnescapeDataString(uri.UserInfo[..sep]);
                builder.Password = Uri.UnescapeDataString(uri.UserInfo[(sep + 1)..]);
            }
            else
            {
                builder.Username = Uri.UnescapeDataString(uri.UserInfo);
            }
        }

        var isLoopback = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                         uri.Host == "127.0.0.1";
        builder.SslMode = isLoopback ? SslMode.Disable : SslMode.Require;

        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = (eq >= 0 ? pair[..eq] : pair).Trim().ToLowerInvariant();
                var v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]).Trim() : string.Empty;

                if (k == "sslmode" && Enum.TryParse<SslMode>(v, ignoreCase: true, out var parsedSsl))
                {
                    builder.SslMode = parsedSsl;
                }
            }
        }

        return builder.ConnectionString;
    }

    private async Task EnsureTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_tableEnsured)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_tableEnsured)
                return;

            const string sql = """
                CREATE TABLE IF NOT EXISTS youlink_state_snapshots (
                    state_key TEXT PRIMARY KEY,
                    payload_json TEXT NOT NULL,
                    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
            _tableEnsured = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAllSnapshotsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn, ct);

        const string sql = "SELECT state_key, payload_json FROM youlink_state_snapshots;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
        }
        return result;
    }

    public async Task UpsertSnapshotAsync(string stateKey, string payload, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn, ct);

        const string sql = """
            INSERT INTO youlink_state_snapshots (state_key, payload_json, updated_at)
            VALUES (@k, @p, NOW())
            ON CONFLICT (state_key)
            DO UPDATE SET payload_json = EXCLUDED.payload_json, updated_at = NOW();
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", stateKey);
        cmd.Parameters.AddWithValue("p", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteSnapshotAsync(string stateKey, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn, ct);

        const string sql = "DELETE FROM youlink_state_snapshots WHERE state_key = @k;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", stateKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Alternativa gratuita via Supabase Storage (bucket privado `youlink-state` criado automaticamente por HTTPS REST).
/// Não exige abertura de portas TCP/IPv6 nem scripts SQL manuais.
/// </summary>
public sealed class SupabaseBucketStateBackend : ICloudStateBackend
{
    public const string DefaultBucketName = "youlink-state";

    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly string _bucketName;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private volatile bool _bucketReady;

    public string ProviderName => "SupabaseStorage";

    public SupabaseBucketStateBackend(
        string supabaseUrl,
        string serviceRoleKey,
        HttpClient? httpClient = null,
        string bucketName = DefaultBucketName)
    {
        _supabaseUrl = supabaseUrl.Trim().TrimEnd('/');
        _serviceRoleKey = serviceRoleKey.Trim();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _bucketName = string.IsNullOrWhiteSpace(bucketName) ? DefaultBucketName : bucketName.Trim();
    }

    private void ApplyAuthHeaders(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        req.Headers.TryAddWithoutValidation("apikey", _serviceRoleKey);
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady)
            return;

        await _bucketLock.WaitAsync(ct);
        try
        {
            if (_bucketReady)
                return;

            using var getReq = new HttpRequestMessage(HttpMethod.Get, $"{_supabaseUrl}/storage/v1/bucket/{_bucketName}");
            ApplyAuthHeaders(getReq);
            using var getRes = await _httpClient.SendAsync(getReq, ct);
            if (getRes.IsSuccessStatusCode)
            {
                _bucketReady = true;
                return;
            }

            using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/storage/v1/bucket");
            ApplyAuthHeaders(createReq);
            createReq.Content = new StringContent(
                JsonSerializer.Serialize(new { id = _bucketName, name = _bucketName, @public = false }),
                Encoding.UTF8,
                "application/json");
            using var createRes = await _httpClient.SendAsync(createReq, ct);
            if (createRes.IsSuccessStatusCode || createRes.StatusCode == HttpStatusCode.Conflict)
            {
                _bucketReady = true;
            }
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAllSnapshotsAsync(CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in new[] { string.Empty, "tenants" })
        {
            var keys = await ListKeysInPrefixAsync(prefix, ct);
            foreach (var key in keys)
            {
                var content = await DownloadObjectAsync(key, ct);
                if (content is not null)
                    result[key] = content;
            }
        }

        return result;
    }

    private async Task<List<string>> ListKeysInPrefixAsync(string prefix, CancellationToken ct)
    {
        var keys = new List<string>();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/storage/v1/object/list/{_bucketName}");
        ApplyAuthHeaders(req);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { prefix, limit = 1000, offset = 0 }),
            Encoding.UTF8,
            "application/json");

        using var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return keys;

        var raw = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return keys;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameProp))
                continue;

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name) || name == ".emptyFolderPlaceholder")
                continue;

            // Objetos sem metadata (null) no list do Supabase representam pastas
            var isFolder = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Null;
            if (isFolder)
                continue;

            var fullKey = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}/{name}";
            keys.Add(fullKey);
        }

        return keys;
    }

    private async Task<string?> DownloadObjectAsync(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{EscapeObjectPath(key)}");
        ApplyAuthHeaders(req);
        using var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return null;

        return await res.Content.ReadAsStringAsync(ct);
    }

    public async Task UpsertSnapshotAsync(string stateKey, string payload, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{EscapeObjectPath(stateKey)}");
        ApplyAuthHeaders(req);
        req.Headers.TryAddWithoutValidation("x-upsert", "true");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var res = await _httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteSnapshotAsync(string stateKey, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_supabaseUrl}/storage/v1/object/{_bucketName}/{EscapeObjectPath(stateKey)}");
        ApplyAuthHeaders(req);
        using var _ = await _httpClient.SendAsync(req, ct);
    }

    private static string EscapeObjectPath(string stateKey) =>
        string.Join('/', stateKey.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}

/// <summary>
/// Coordena a hidratação na inicialização do servidor e o espelhamento em tempo real dos arquivos JSON em App_Data
/// para o banco externo gratuito, preservando lojas publicadas, pedidos e credenciais após deploys no Render Free.
/// </summary>
public sealed class CloudAppDataMirror : IDisposable
{
    private static readonly Regex SafeSlugFileRegex = new(@"^[a-z0-9][a-z0-9-]*\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> AllowedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "clothingstore_db.json",
        "platform_owner_settings.json",
        "platform_mp_credentials.json",
        "tenants_index.json",
        "cloud_accounts_registry.json",
        "cloud_chat_store.json"
    };

    private readonly string _rootDirectory;
    private readonly ICloudStateBackend _backend;
    private readonly Channel<MirrorOp> _channel = Channel.CreateUnbounded<MirrorOp>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> _flushWaiters = new();
    private int _pendingCount;

    public string RootDirectory => _rootDirectory;
    public string ProviderName => _backend.ProviderName;

    public CloudAppDataMirror(string rootDirectory, ICloudStateBackend backend)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public async Task<int> HydrateToDiskAsync(CancellationToken ct = default)
    {
        var snapshots = await _backend.LoadAllSnapshotsAsync(ct);
        if (snapshots.Count == 0)
            return 0;

        Directory.CreateDirectory(_rootDirectory);
        var hasEnvEncryptionKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_TOKEN_ENCRYPTION_KEY"));
        var restored = 0;

        foreach (var (stateKey, payload) in snapshots)
        {
            if (hasEnvEncryptionKey && IsKeystoreStateKey(stateKey))
                continue;

            var targetPath = ResolveSafeLocalPath(stateKey);
            if (targetPath is null || string.IsNullOrWhiteSpace(payload))
                continue;

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var temp = $"{targetPath}.{Guid.NewGuid():N}.hydrate.tmp";
            await File.WriteAllTextAsync(temp, payload, Encoding.UTF8, ct);
            File.Move(temp, targetPath, overwrite: true);
            restored++;
        }

        return restored;
    }

    public void EnqueueUpsert(string fullFilePath, string content)
    {
        var stateKey = TryNormalizeStateKey(fullFilePath);
        if (stateKey is null)
            return;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_TOKEN_ENCRYPTION_KEY")) &&
            IsKeystoreStateKey(stateKey))
        {
            return;
        }

        Interlocked.Increment(ref _pendingCount);
        _channel.Writer.TryWrite(new MirrorOp(stateKey, content, IsDelete: false));
    }

    public void EnqueueDelete(string fullFilePath)
    {
        var stateKey = TryNormalizeStateKey(fullFilePath);
        if (stateKey is null)
            return;

        Interlocked.Increment(ref _pendingCount);
        _channel.Writer.TryWrite(new MirrorOp(stateKey, null, IsDelete: true));
    }

    public async Task FlushAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _pendingCount) == 0)
            return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushWaiters.Enqueue(tcs);

        // Dispara verificação caso a fila já tenha zerado entre a checagem e o Enqueue
        if (Volatile.Read(ref _pendingCount) == 0 && _flushWaiters.TryPeek(out _))
            SignalWaitersIfDrained();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public string? TryNormalizeStateKey(string fullFilePath)
    {
        if (string.IsNullOrWhiteSpace(fullFilePath))
            return null;

        try
        {
            var resolved = Path.GetFullPath(fullFilePath);
            var relative = Path.GetRelativePath(_rootDirectory, resolved).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;

            if (relative == ".mp_keystore.key")
                return "keystore_tenants.key";
            if (relative == "token_master.key")
                return "keystore_platform.key";
            if (AllowedRootFiles.Contains(relative))
                return relative.ToLowerInvariant();

            if (relative.StartsWith("tenants/", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = relative["tenants/".Length..];
                if (!fileName.Contains('/') && SafeSlugFileRegex.IsMatch(fileName))
                    return $"tenants/{fileName.ToLowerInvariant()}";
            }
        }
        catch
        {
        }

        return null;
    }

    public string? ResolveSafeLocalPath(string stateKey)
    {
        if (string.IsNullOrWhiteSpace(stateKey))
            return null;

        var normalized = stateKey.Trim().Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal))
            return null;

        if (normalized == "keystore_tenants.key" || normalized == ".mp_keystore.key")
            return Path.Combine(_rootDirectory, ".mp_keystore.key");
        if (normalized == "keystore_platform.key" || normalized == "token_master.key")
            return Path.Combine(_rootDirectory, "token_master.key");
        if (AllowedRootFiles.Contains(normalized))
            return Path.Combine(_rootDirectory, normalized.ToLowerInvariant());

        if (normalized.StartsWith("tenants/", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = normalized["tenants/".Length..];
            if (!fileName.Contains('/') && SafeSlugFileRegex.IsMatch(fileName))
                return Path.Combine(_rootDirectory, "tenants", fileName.ToLowerInvariant());
        }

        return null;
    }

    private static bool IsKeystoreStateKey(string stateKey) =>
        stateKey is "keystore_tenants.key" or "keystore_platform.key" or ".mp_keystore.key" or "token_master.key";

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var op))
                {
                    try
                    {
                        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        opCts.CancelAfter(TimeSpan.FromSeconds(8));
                        if (op.IsDelete)
                        {
                            await _backend.DeleteSnapshotAsync(op.StateKey, opCts.Token);
                        }
                        else if (op.Payload is not null)
                        {
                            await _backend.UpsertSnapshotAsync(op.StateKey, op.Payload, opCts.Token);
                        }
                    }
                    catch
                    {
                        // Melhor esforço: falha transitória na nuvem nunca derruba a operação local
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        SignalWaitersIfDrained();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SignalWaitersIfDrained()
    {
        if (Volatile.Read(ref _pendingCount) > 0)
            return;

        while (_flushWaiters.TryDequeue(out var waiter))
        {
            waiter.TrySetResult(true);
        }
    }

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
            FlushAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
        }
        finally
        {
            _cts.Cancel();
            try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }

    private sealed record MirrorOp(string StateKey, string? Payload, bool IsDelete);
}

/// <summary>
/// Registro por diretório raiz (`App_Data`) para espelhamento automático transparente sem quebrar assinaturas de construtor.
/// </summary>
public static class CloudAppDataMirrorRegistry
{
    private static readonly ConcurrentDictionary<string, CloudAppDataMirror> Mirrors = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string rootDirectory, CloudAppDataMirror mirror)
    {
        var key = NormalizeDir(rootDirectory);
        Mirrors[key] = mirror;
    }

    public static void Unregister(string rootDirectory)
    {
        var key = NormalizeDir(rootDirectory);
        Mirrors.TryRemove(key, out _);
    }

    public static CloudAppDataMirror? ConfigureFromEnvironment(string rootDirectory, IConfiguration? configuration = null)
    {
        var postgresConn = Environment.GetEnvironmentVariable("YOULINK_DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("POSTGRES_URL")
            ?? configuration?["Database:PostgresUrl"]
            ?? configuration?["ConnectionStrings:DefaultConnection"];

        ICloudStateBackend? backend = null;
        if (!string.IsNullOrWhiteSpace(postgresConn) &&
            !postgresConn.Contains("seu-usuario", StringComparison.OrdinalIgnoreCase))
        {
            backend = new PostgresCloudStateBackend(postgresConn);
        }
        else
        {
            var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL") ?? configuration?["Supabase:Url"];
            var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY") ?? configuration?["Supabase:ServiceRoleKey"];

            if (!string.IsNullOrWhiteSpace(supabaseUrl) &&
                !string.IsNullOrWhiteSpace(supabaseKey) &&
                !supabaseUrl.Contains("seu-projeto.supabase.co", StringComparison.OrdinalIgnoreCase))
            {
                backend = new SupabaseBucketStateBackend(supabaseUrl, supabaseKey);
            }
        }

        if (backend is null)
            return null;

        var mirror = new CloudAppDataMirror(rootDirectory, backend);
        Register(rootDirectory, mirror);
        return mirror;
    }

    public static void TryNotifySaved(string filePath, string content)
    {
        var mirror = FindMirrorForFile(filePath);
        mirror?.EnqueueUpsert(filePath, content);
    }

    public static void TryNotifyDeleted(string filePath)
    {
        var mirror = FindMirrorForFile(filePath);
        mirror?.EnqueueDelete(filePath);
    }

    private static CloudAppDataMirror? FindMirrorForFile(string filePath)
    {
        if (Mirrors.IsEmpty || string.IsNullOrWhiteSpace(filePath))
            return null;

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            while (!string.IsNullOrWhiteSpace(dir))
            {
                if (Mirrors.TryGetValue(NormalizeDir(dir), out var mirror))
                    return mirror;

                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string NormalizeDir(string dir) =>
        Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
