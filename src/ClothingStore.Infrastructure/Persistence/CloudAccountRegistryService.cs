using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Registro central na nuvem das contas de lojistas para login multi-dispositivo
/// (Windows, Android e reinstalações do app), espelhado via <see cref="CloudAppDataMirrorRegistry"/>.
/// </summary>
public sealed class CloudAccountRegistryService
{
    public const string RegistryFileName = "cloud_accounts_registry.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CloudAccountsRegistryState _state = new();

    public CloudAccountRegistryService(string rootDirectory, IPasswordHasher passwordHasher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _filePath = Path.Combine(Path.GetFullPath(rootDirectory), RegistryFileName);
    }

    public string RegistryFilePath => _filePath;

    public async Task<CloudAccountUpsertResult> UpsertAccountAsync(
        CloudAccountCredentialSyncDto dto,
        bool hasValidSyncKey = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var email = NormalizeIdentifier(dto.Email);
        var fullName = (dto.FullName ?? string.Empty).Trim();
        var passwordHash = (dto.PasswordHash ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return new CloudAccountUpsertResult(
                Success: false,
                StatusCode: 400,
                Message: "Nome completo, e-mail e hash de senha são obrigatórios.");
        }

        var username = NormalizeIdentifier(dto.Username);
        if (string.IsNullOrWhiteSpace(username))
        {
            username = User.DeriveDefaultUsername(email);
        }

        var prevEmail = NormalizeIdentifier(dto.PreviousEmail);
        var prevHash = (dto.PreviousPasswordHash ?? string.Empty).Trim();

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();

            CloudAccountEntry? existing = null;
            if (!string.IsNullOrWhiteSpace(prevEmail))
            {
                existing = _state.Accounts.FirstOrDefault(a =>
                    string.Equals(NormalizeIdentifier(a.Email), prevEmail, StringComparison.OrdinalIgnoreCase));
            }

            existing ??= _state.Accounts.FirstOrDefault(a =>
                string.Equals(NormalizeIdentifier(a.Email), email, StringComparison.OrdinalIgnoreCase));

            var usernameConflict = _state.Accounts.FirstOrDefault(a =>
                !ReferenceEquals(a, existing) &&
                !string.IsNullOrWhiteSpace(username) &&
                string.Equals(NormalizeIdentifier(a.Username), username, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(NormalizeIdentifier(a.Email), email, StringComparison.OrdinalIgnoreCase));

            if (usernameConflict is not null)
            {
                return new CloudAccountUpsertResult(
                    Success: false,
                    StatusCode: 409,
                    Message: "Este nome de usuário já está em uso por outra conta.");
            }

            if (existing is not null)
            {
                var hashMatchesCurrent = string.Equals(existing.PasswordHash, passwordHash, StringComparison.Ordinal);
                var hashMatchesPrevious = !string.IsNullOrWhiteSpace(prevHash) &&
                                          string.Equals(existing.PasswordHash, prevHash, StringComparison.Ordinal);

                if (!hasValidSyncKey && !hashMatchesCurrent && !hashMatchesPrevious)
                {
                    return new CloudAccountUpsertResult(
                        Success: false,
                        StatusCode: 401,
                        Message: "Já existe uma conta na nuvem com este e-mail. Use a aba Entrar ou recupere sua senha.");
                }

                existing.Email = email;
                existing.Username = username;
                existing.FullName = fullName;
                existing.PasswordHash = passwordHash;
                existing.Phone = TrimOrNull(dto.Phone) ?? existing.Phone;
                existing.StoreName = TrimOrNull(dto.StoreName) ?? existing.StoreName;
                existing.StoreSlug = TrimOrNull(dto.StoreSlug) ?? existing.StoreSlug;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _state.Accounts.Add(new CloudAccountEntry
                {
                    Email = email,
                    Username = username,
                    FullName = fullName,
                    PasswordHash = passwordHash,
                    Phone = TrimOrNull(dto.Phone),
                    StoreName = TrimOrNull(dto.StoreName),
                    StoreSlug = TrimOrNull(dto.StoreSlug),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            SaveToDiskUnsafe();
            return new CloudAccountUpsertResult(Success: true, StatusCode: 200, Message: "Conta sincronizada na nuvem com sucesso.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CloudVerifyLoginResponseDto> VerifyLoginAsync(
        LoginRequest request,
        CancellationToken ct = default)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return new CloudVerifyLoginResponseDto(
                Verified: false,
                AccountExists: false,
                Account: null,
                Message: "Informe usuário/e-mail e senha.");
        }

        var normalized = NormalizeIdentifier(request.Email);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new CloudVerifyLoginResponseDto(false, false, null, "Credenciais inválidas.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();

            var match = _state.Accounts.FirstOrDefault(a =>
                string.Equals(NormalizeIdentifier(a.Email), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeIdentifier(a.Username), normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return new CloudVerifyLoginResponseDto(
                    Verified: false,
                    AccountExists: false,
                    Account: null,
                    Message: "Conta não encontrada no registro central.");
            }

            if (!_passwordHasher.VerifyPassword(request.Password, match.PasswordHash))
            {
                return new CloudVerifyLoginResponseDto(
                    Verified: false,
                    AccountExists: true,
                    Account: null,
                    Message: "Credenciais inválidas. Verifique seu e-mail e senha.");
            }

            return new CloudVerifyLoginResponseDto(
                Verified: true,
                AccountExists: true,
                Account: new CloudAccountCredentialSyncDto(
                    match.FullName,
                    match.Username,
                    match.Email,
                    match.PasswordHash,
                    match.Phone,
                    match.StoreName,
                    match.StoreSlug),
                Message: "Conta verificada com sucesso.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> PurgeAllAccountsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var removed = _state.Accounts.Count;
            _state.Accounts.Clear();
            SaveToDiskUnsafe();
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            return _state.Accounts.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void RefreshFromDiskIfModifiedUnsafe()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var lastWrite = File.GetLastWriteTimeUtc(_filePath);
            if (lastWrite <= _lastLoadedUtc)
                return;

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<CloudAccountsRegistryState>(json, JsonOptions);
            if (parsed is not null)
            {
                parsed.Accounts ??= [];
                _state = parsed;
                _lastLoadedUtc = lastWrite;
            }
        }
        catch
        {
        }
    }

    private void SaveToDiskUnsafe()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_state, JsonOptions);
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _filePath, overwrite: true);
            CloudAppDataMirrorRegistry.TryNotifySaved(_filePath, json);
            _lastLoadedUtc = File.GetLastWriteTimeUtc(_filePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().TrimStart('@').ToLowerInvariant();
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class CloudAccountsRegistryState
    {
        public List<CloudAccountEntry> Accounts { get; set; } = [];
    }

    private sealed class CloudAccountEntry
    {
        public string Email { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? StoreName { get; set; }
        public string? StoreSlug { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed record CloudAccountUpsertResult(bool Success, int StatusCode, string Message);
