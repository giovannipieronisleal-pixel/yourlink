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
                if (hasValidSyncKey && !string.IsNullOrWhiteSpace(dto.PlanTier))
                {
                    existing.PlanTier = StoreSettings.NormalizePlanTier(dto.PlanTier);
                    existing.PlanExpiresAt = dto.PlanExpiresAt;
                    if (dto.IsSiteActive.HasValue)
                        existing.IsSiteActive = dto.IsSiteActive.Value;
                    existing.PlanUpdatedAt = DateTimeOffset.UtcNow;
                }
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
                    PlanTier = hasValidSyncKey && !string.IsNullOrWhiteSpace(dto.PlanTier)
                        ? StoreSettings.NormalizePlanTier(dto.PlanTier)
                        : null,
                    PlanExpiresAt = hasValidSyncKey ? dto.PlanExpiresAt : null,
                    IsSiteActive = hasValidSyncKey ? dto.IsSiteActive : null,
                    PlanUpdatedAt = hasValidSyncKey && !string.IsNullOrWhiteSpace(dto.PlanTier) ? DateTimeOffset.UtcNow : null,
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

            var now = DateTimeOffset.UtcNow;
            EvaluateEntryGraceUnsafe(match, now);
            var effectiveTier = ResolveEffectivePlan(match.PlanTier, match.PlanExpiresAt, now);
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
                    match.StoreSlug,
                    PlanTier: effectiveTier,
                    PlanExpiresAt: effectiveTier == StoreSettings.PlanTierBasic ? null : match.PlanExpiresAt,
                    IsSiteActive: match.IsSiteActive ?? true,
                    IsCourtesyVip: string.Equals(match.PlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase),
                    PlanRevokedAt: match.PlanRevokedAt,
                    IsRecurringSubscription: match.IsRecurringSubscription,
                    BillingGraceEndsAt: match.BillingGraceEndsAt,
                    IsBillingSuspended: match.IsBillingSuspended,
                    BillingWarningMessage: match.BillingWarningMessage),
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

    public async Task<IReadOnlyList<CloudAccountSummary>> GetAllAccountsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var now = DateTimeOffset.UtcNow;
            var anyChanged = false;
            foreach (var acc in _state.Accounts)
            {
                if (EvaluateEntryGraceUnsafe(acc, now))
                    anyChanged = true;
            }
            if (anyChanged)
                SaveToDiskUnsafe();

            return _state.Accounts
                .Select(MapToSummary)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CloudAccountSummary?> ApplyPlanAsync(
        string? targetEmail,
        string? targetUsername,
        string? storeName,
        string planTier,
        int? durationDays,
        bool extendExistingDuration,
        bool activateSite,
        CancellationToken ct = default,
        bool isCourtesyVip = false,
        bool isRecurringSubscription = false)
    {
        var normalizedTier = StoreSettings.NormalizePlanTier(planTier);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var match = FindMatchingEntryUnsafe(targetEmail, targetUsername, storeName, storeSlug: null);
            if (match is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            var isBasicRemoval = string.Equals(normalizedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);

            if (isBasicRemoval)
            {
                match.PlanTier = StoreSettings.PlanTierBasic;
                match.PlanExpiresAt = null;
                match.PlanBillingKind = null;
                match.PlanRevokedAt = now;
                match.IsRecurringSubscription = false;
                match.BillingGraceEndsAt = null;
                match.IsBillingSuspended = false;
                match.BillingWarningMessage = null;
                if (activateSite)
                    match.IsSiteActive = true;
            }
            else
            {
                DateTimeOffset? nextExpiry = null;
                if (durationDays.HasValue && durationDays.Value > 0)
                {
                    var baseStart = extendExistingDuration &&
                                    string.Equals(match.PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase) &&
                                    match.PlanExpiresAt.HasValue &&
                                    match.PlanExpiresAt.Value > now
                        ? match.PlanExpiresAt.Value
                        : now;
                    nextExpiry = baseStart.AddDays(durationDays.Value);
                }

                match.PlanTier = normalizedTier;
                match.PlanExpiresAt = nextExpiry;
                match.PlanBillingKind = isCourtesyVip
                    ? PlanActivationCode.BillingKindGiftedUnpaid
                    : PlanActivationCode.BillingKindPaid;
                match.PlanRevokedAt = null;
                match.IsRecurringSubscription = !isCourtesyVip && isRecurringSubscription;
                match.BillingGraceEndsAt = null;
                match.IsBillingSuspended = false;
                match.BillingWarningMessage = null;
                if (activateSite)
                    match.IsSiteActive = true;
            }

            if (!string.IsNullOrWhiteSpace(storeName) && string.IsNullOrWhiteSpace(match.StoreName))
                match.StoreName = storeName.Trim();
            match.PlanUpdatedAt = now;
            match.UpdatedAt = now;

            SaveToDiskUnsafe();
            return MapToSummary(match);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CloudAccountSummary?> MarkRecurringPaymentFailureOrGraceAsync(
        string? targetEmail,
        string? targetUsername,
        string? storeName,
        DateTimeOffset? dueAt = null,
        string? failureReason = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var match = FindMatchingEntryUnsafe(targetEmail, targetUsername, storeName, storeSlug: null);
            if (match is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            var anchor = dueAt ?? match.PlanExpiresAt ?? now;
            var targetGraceEnd = anchor.AddDays(StoreSettings.SubscriptionBillingGraceDays);
            if (match.BillingGraceEndsAt.HasValue && match.BillingGraceEndsAt.Value < targetGraceEnd)
                targetGraceEnd = match.BillingGraceEndsAt.Value;

            match.IsRecurringSubscription = true;
            match.BillingGraceEndsAt = targetGraceEnd;
            if (!string.IsNullOrWhiteSpace(failureReason))
                match.BillingWarningMessage = failureReason.Trim();

            EvaluateEntryGraceUnsafe(match, now);
            match.PlanUpdatedAt = now;
            match.UpdatedAt = now;
            SaveToDiskUnsafe();
            return MapToSummary(match);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CloudPlanStatusDto?> GetPlanStatusAsync(
        string? email,
        string? username,
        string? storeSlug,
        string? storeName = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var match = FindMatchingEntryUnsafe(email, username, storeName, storeSlug);
            if (match is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            if (EvaluateEntryGraceUnsafe(match, now))
                SaveToDiskUnsafe();

            var effectiveTier = ResolveEffectivePlan(match.PlanTier, match.PlanExpiresAt, now);
            return new CloudPlanStatusDto(
                PlanTier: effectiveTier,
                PlanExpiresAt: effectiveTier == StoreSettings.PlanTierBasic ? null : match.PlanExpiresAt,
                IsSiteActive: match.IsSiteActive ?? true,
                StoreName: match.StoreName,
                StoreSlug: match.StoreSlug,
                IsCourtesyVip: string.Equals(match.PlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase),
                PlanRevokedAt: match.PlanRevokedAt,
                IsRecurringSubscription: match.IsRecurringSubscription,
                BillingGraceEndsAt: match.BillingGraceEndsAt,
                IsBillingSuspended: match.IsBillingSuspended,
                BillingWarningMessage: match.BillingWarningMessage);
        }
        finally
        {
            _lock.Release();
        }
    }

    private CloudAccountEntry? FindMatchingEntryUnsafe(
        string? email,
        string? username,
        string? storeName,
        string? storeSlug)
    {
        var normEmail = NormalizeIdentifier(email);
        if (!string.IsNullOrWhiteSpace(normEmail))
        {
            var byEmail = _state.Accounts.FirstOrDefault(a =>
                string.Equals(NormalizeIdentifier(a.Email), normEmail, StringComparison.OrdinalIgnoreCase));
            if (byEmail is not null)
                return byEmail;
        }

        var normUsername = NormalizeIdentifier(username);
        if (!string.IsNullOrWhiteSpace(normUsername) && normUsername != "-")
        {
            var byUser = _state.Accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Username) &&
                string.Equals(NormalizeIdentifier(a.Username), normUsername, StringComparison.OrdinalIgnoreCase));
            if (byUser is not null)
                return byUser;
        }

        var trimmedSlug = TrimOrNull(storeSlug);
        if (!string.IsNullOrWhiteSpace(trimmedSlug))
        {
            var generatedSlug = Category.GenerateSlug(trimmedSlug);
            var bySlug = _state.Accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.StoreSlug) &&
                (string.Equals(a.StoreSlug.Trim(), trimmedSlug, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Category.GenerateSlug(a.StoreSlug), generatedSlug, StringComparison.OrdinalIgnoreCase)));
            if (bySlug is not null)
                return bySlug;
        }

        var trimmedStore = TrimOrNull(storeName);
        if (!string.IsNullOrWhiteSpace(trimmedStore))
        {
            var generatedStoreSlug = Category.GenerateSlug(trimmedStore);
            var byStore = _state.Accounts.FirstOrDefault(a =>
                (!string.IsNullOrWhiteSpace(a.StoreName) &&
                 string.Equals(a.StoreName.Trim(), trimmedStore, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(a.StoreSlug) &&
                 string.Equals(Category.GenerateSlug(a.StoreSlug), generatedStoreSlug, StringComparison.OrdinalIgnoreCase)));
            if (byStore is not null)
                return byStore;
        }

        return null;
    }

    private static bool EvaluateEntryGraceUnsafe(CloudAccountEntry entry, DateTimeOffset now)
    {
        if (!entry.IsRecurringSubscription && !entry.BillingGraceEndsAt.HasValue && !entry.IsBillingSuspended)
            return false;

        if (!entry.BillingGraceEndsAt.HasValue)
        {
            if (!entry.PlanExpiresAt.HasValue || now < entry.PlanExpiresAt.Value)
                return false;
        }

        var graceEnd = entry.BillingGraceEndsAt ?? entry.PlanExpiresAt!.Value.AddDays(StoreSettings.SubscriptionBillingGraceDays);
        if (now < graceEnd)
        {
            if (entry.BillingGraceEndsAt != graceEnd || entry.IsBillingSuspended)
            {
                entry.BillingGraceEndsAt = graceEnd;
                entry.IsBillingSuspended = false;
                entry.BillingWarningMessage ??= $"Renovação mensal pendente no Mercado Pago. Regularize em até {StoreSettings.SubscriptionBillingGraceDays} dias.";
                entry.UpdatedAt = now;
                return true;
            }
            return false;
        }

        if (!entry.IsBillingSuspended || (entry.IsSiteActive ?? true) || entry.BillingGraceEndsAt != graceEnd)
        {
            entry.BillingGraceEndsAt = graceEnd;
            entry.IsBillingSuspended = true;
            entry.IsSiteActive = false;
            entry.BillingWarningMessage = "Assinatura vencida há mais de 5 dias sem pagamento no Mercado Pago. Conta e loja desativadas até a regularização.";
            entry.UpdatedAt = now;
            return true;
        }

        return false;
    }

    private static CloudAccountSummary MapToSummary(CloudAccountEntry a) =>
        new(
            a.FullName,
            a.Username,
            a.Email,
            a.Phone,
            a.StoreName,
            a.StoreSlug,
            a.CreatedAt,
            a.UpdatedAt,
            a.PlanTier,
            a.PlanExpiresAt,
            a.IsSiteActive ?? true,
            a.PlanUpdatedAt,
            string.Equals(a.PlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase),
            a.PlanRevokedAt,
            a.IsRecurringSubscription,
            a.BillingGraceEndsAt,
            a.IsBillingSuspended,
            a.BillingWarningMessage);

    public static string ResolveEffectivePlan(string? rawTier, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var normalized = StoreSettings.NormalizePlanTier(rawTier);
        if (expiresAt.HasValue && now >= expiresAt.Value)
            return StoreSettings.PlanTierBasic;

        return normalized;
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
        public string? PlanTier { get; set; }
        public DateTimeOffset? PlanExpiresAt { get; set; }
        public bool? IsSiteActive { get; set; }
        public DateTimeOffset? PlanUpdatedAt { get; set; }
        public string? PlanBillingKind { get; set; }
        public DateTimeOffset? PlanRevokedAt { get; set; }
        public bool IsRecurringSubscription { get; set; }
        public DateTimeOffset? BillingGraceEndsAt { get; set; }
        public bool IsBillingSuspended { get; set; }
        public string? BillingWarningMessage { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed record CloudAccountUpsertResult(bool Success, int StatusCode, string Message);

public sealed record CloudAccountSummary(
    string FullName,
    string Username,
    string Email,
    string? Phone,
    string? StoreName,
    string? StoreSlug,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlanTier = null,
    DateTimeOffset? PlanExpiresAt = null,
    bool IsSiteActive = true,
    DateTimeOffset? PlanUpdatedAt = null,
    bool IsCourtesyVip = false,
    DateTimeOffset? PlanRevokedAt = null,
    bool IsRecurringSubscription = false,
    DateTimeOffset? BillingGraceEndsAt = null,
    bool IsBillingSuspended = false,
    string? BillingWarningMessage = null);
