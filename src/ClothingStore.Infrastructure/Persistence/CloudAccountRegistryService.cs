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
    public const int MinimumCloudPasswordLength = 8;
    public const int MaxFailedLoginAttempts = 5;
    public static readonly TimeSpan LoginLockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SessionTokenLifetime = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IPasswordHasher _passwordHasher;
    private readonly string _dummyPasswordHash;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CloudAccountsRegistryState _state = new();

    public CloudAccountRegistryService(string rootDirectory, IPasswordHasher passwordHasher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _dummyPasswordHash = _passwordHasher.HashPassword("YouLinkDummyTimingGuard#2026!");
        _filePath = Path.Combine(Path.GetFullPath(rootDirectory), RegistryFileName);
    }

    public string RegistryFilePath => _filePath;

    public async Task<CloudAccountUpsertResult> UpsertAccountAsync(
        CloudAccountCredentialSyncDto dto,
        bool hasValidSyncKey = false,
        CancellationToken ct = default,
        bool emailVerifiedByServer = false,
        string? callerCloudSessionToken = null)
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
                Message: "Nome completo, e-mail e credencial de senha são obrigatórios.");
        }

        var username = NormalizeIdentifier(dto.Username);
        if (string.IsNullOrWhiteSpace(username))
        {
            username = User.DeriveDefaultUsername(email);
        }

        var prevEmail = NormalizeIdentifier(dto.PreviousEmail);
        var effectiveToken = !string.IsNullOrWhiteSpace(callerCloudSessionToken)
            ? callerCloudSessionToken.Trim()
            : dto.CloudSessionToken?.Trim();

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

            existing ??= _state.Accounts.FirstOrDefault(a =>
                string.IsNullOrWhiteSpace(a.PasswordHash) &&
                ((!string.IsNullOrWhiteSpace(username) &&
                  string.Equals(NormalizeIdentifier(a.Username), username, StringComparison.OrdinalIgnoreCase) &&
                  (string.IsNullOrWhiteSpace(a.Email) || string.Equals(NormalizeIdentifier(a.Email), email, StringComparison.OrdinalIgnoreCase))) ||
                 (string.IsNullOrWhiteSpace(a.Email) &&
                  string.IsNullOrWhiteSpace(a.Username) &&
                  !string.IsNullOrWhiteSpace(dto.StoreName) &&
                  !IsGenericStoreIdentifier(dto.StoreName) &&
                  string.Equals(a.StoreName?.Trim(), dto.StoreName.Trim(), StringComparison.OrdinalIgnoreCase))));

            var usernameConflict = _state.Accounts.FirstOrDefault(a =>
                !ReferenceEquals(a, existing) &&
                !string.IsNullOrWhiteSpace(username) &&
                string.Equals(NormalizeIdentifier(a.Username), username, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(a.Email) &&
                !string.Equals(NormalizeIdentifier(a.Email), email, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(a.PasswordHash));

            if (usernameConflict is not null)
            {
                return new CloudAccountUpsertResult(
                    Success: false,
                    StatusCode: 409,
                    Message: "Este nome de usuário já está em uso por outra conta.");
            }

            var emailConflict = _state.Accounts.FirstOrDefault(a =>
                !ReferenceEquals(a, existing) &&
                !string.IsNullOrWhiteSpace(a.Email) &&
                string.Equals(NormalizeIdentifier(a.Email), email, StringComparison.OrdinalIgnoreCase));

            if (emailConflict is not null)
            {
                return new CloudAccountUpsertResult(
                    Success: false,
                    StatusCode: 409,
                    Message: "Este e-mail já está em uso por outra conta.");
            }

            var now = DateTimeOffset.UtcNow;
            string? issuedToken = null;
            DateTimeOffset? issuedExpiresAt = null;

            if (existing is not null)
            {
                var isClaimingUnregisteredEntitlement = string.IsNullOrWhiteSpace(existing.PasswordHash);
                var isEmailChange = !string.IsNullOrWhiteSpace(existing.Email) &&
                                    !string.Equals(NormalizeIdentifier(existing.Email), email, StringComparison.OrdinalIgnoreCase);

                if (isClaimingUnregisteredEntitlement)
                {
                    // Segurança crítica (Item A): todo cadastro/assunção de conta exige confirmação
                    // prévia de posse do e-mail (OTP 6 dígitos) antes de gravar senha ou assumir plano.
                    if (!hasValidSyncKey && !emailVerifiedByServer)
                    {
                        var hasPreProvisionedPaidOrCourtesyPlan =
                            !string.IsNullOrWhiteSpace(existing.PlanTier) &&
                            !string.Equals(StoreSettings.NormalizePlanTier(existing.PlanTier), StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);

                        return new CloudAccountUpsertResult(
                            Success: false,
                            StatusCode: 403,
                            Message: hasPreProvisionedPaidOrCourtesyPlan
                                ? "Este e-mail possui um plano pré-liberado. Confirme o código de verificação enviado para o seu e-mail antes de concluir o cadastro."
                                : "Confirme o código de verificação de 6 dígitos enviado para o seu e-mail antes de concluir o cadastro.",
                            RequiresEmailVerification: true);
                    }
                }
                else
                {
                    // Segurança crítica (Item 3 — anti pass-the-hash): contas já registradas NÃO aceitam
                    // comparação direta de PasswordHash para alterar dados/senha; exigem CloudSessionToken válido ou SyncKey.
                    var hasValidAccountSession = VerifyAccountSessionTokenUnsafe(existing, effectiveToken, now);
                    if (!hasValidSyncKey && !hasValidAccountSession)
                    {
                        return new CloudAccountUpsertResult(
                            Success: false,
                            StatusCode: 401,
                            Message: "Já existe uma conta na nuvem com este e-mail. Use a aba Entrar ou recupere sua senha.");
                    }

                    // Segurança crítica (Item A): troca de e-mail exige confirmação por código OTP no novo endereço.
                    if (isEmailChange && !hasValidSyncKey && !emailVerifiedByServer)
                    {
                        return new CloudAccountUpsertResult(
                            Success: false,
                            StatusCode: 403,
                            Message: "Para alterar o e-mail da conta, confirme primeiro o código de verificação de 6 dígitos enviado para o novo endereço de e-mail.",
                            RequiresEmailVerification: true);
                    }
                }

                var passwordChanged = !string.Equals(existing.PasswordHash, passwordHash, StringComparison.Ordinal);
                existing.Email = email;
                existing.Username = username;
                existing.FullName = fullName;
                existing.PasswordHash = passwordHash;
                existing.Phone = TrimOrNull(dto.Phone) ?? existing.Phone;
                existing.StoreName = (!IsGenericStoreIdentifier(dto.StoreName) ? TrimOrNull(dto.StoreName) : null) ?? existing.StoreName;
                existing.StoreSlug = (!IsGenericStoreIdentifier(dto.StoreSlug) ? TrimOrNull(dto.StoreSlug) : null) ?? existing.StoreSlug;
                if (emailVerifiedByServer)
                {
                    existing.IsEmailVerified = true;
                    existing.EmailVerifiedAt = isEmailChange ? now : (existing.EmailVerifiedAt ?? now);
                }
                else if (isEmailChange)
                {
                    existing.IsEmailVerified = false;
                    existing.EmailVerifiedAt = null;
                }

                if (hasValidSyncKey && !string.IsNullOrWhiteSpace(dto.PlanTier))
                {
                    existing.PlanTier = StoreSettings.NormalizePlanTier(dto.PlanTier);
                    existing.PlanExpiresAt = dto.PlanExpiresAt;
                    if (dto.IsSiteActive.HasValue)
                        existing.IsSiteActive = dto.IsSiteActive.Value;
                    existing.PlanUpdatedAt = now;
                }

                if (passwordChanged || isEmailChange)
                {
                    existing.Sessions.Clear();
                    existing.FailedLoginAttempts = 0;
                    existing.LockoutEndsAt = null;
                }

                (issuedToken, issuedExpiresAt) = IssueSessionTokenUnsafe(existing, now);
                existing.UpdatedAt = now;
            }
            else
            {
                if (!hasValidSyncKey && !emailVerifiedByServer)
                {
                    return new CloudAccountUpsertResult(
                        Success: false,
                        StatusCode: 403,
                        Message: "Confirme o código de verificação de 6 dígitos enviado para o seu e-mail antes de concluir o cadastro.",
                        RequiresEmailVerification: true);
                }

                var created = new CloudAccountEntry
                {
                    Email = email,
                    Username = username,
                    FullName = fullName,
                    PasswordHash = passwordHash,
                    Phone = TrimOrNull(dto.Phone),
                    StoreName = TrimOrNull(dto.StoreName),
                    StoreSlug = TrimOrNull(dto.StoreSlug),
                    IsEmailVerified = emailVerifiedByServer,
                    EmailVerifiedAt = emailVerifiedByServer ? now : null,
                    PlanTier = hasValidSyncKey && !string.IsNullOrWhiteSpace(dto.PlanTier)
                        ? StoreSettings.NormalizePlanTier(dto.PlanTier)
                        : null,
                    PlanExpiresAt = hasValidSyncKey ? dto.PlanExpiresAt : null,
                    IsSiteActive = hasValidSyncKey ? dto.IsSiteActive : null,
                    PlanUpdatedAt = hasValidSyncKey && !string.IsNullOrWhiteSpace(dto.PlanTier) ? now : null,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                (issuedToken, issuedExpiresAt) = IssueSessionTokenUnsafe(created, now);
                _state.Accounts.Add(created);
            }

            SaveToDiskUnsafe();
            return new CloudAccountUpsertResult(
                Success: true,
                StatusCode: 200,
                Message: "Conta sincronizada na nuvem com sucesso.",
                CloudSessionToken: issuedToken,
                CloudSessionExpiresAt: issuedExpiresAt);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Verified, string? Email, string? Username, string? StoreSlug, bool IsEmailVerified)> ValidateSessionTokenAsync(
        string? rawToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return (false, null, null, null, false);

        var tokenHash = HashSessionTokenSha256(rawToken.Trim());
        var tokenHashBytes = Encoding.ASCII.GetBytes(tokenHash);
        var now = DateTimeOffset.UtcNow;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            foreach (var acc in _state.Accounts)
            {
                if (acc.Sessions is null || acc.Sessions.Count == 0)
                    continue;

                foreach (var session in acc.Sessions)
                {
                    if (session.ExpiresAt <= now || string.IsNullOrWhiteSpace(session.TokenHash))
                        continue;

                    var storedBytes = Encoding.ASCII.GetBytes(session.TokenHash);
                    if (storedBytes.Length == tokenHashBytes.Length &&
                        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, tokenHashBytes))
                    {
                        return (true, acc.Email, acc.Username, acc.StoreSlug, acc.IsEmailVerified);
                    }
                }
            }

            return (false, null, null, null, false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsEmailVerifiedAsync(string? email, CancellationToken ct = default)
    {
        var normalized = NormalizeIdentifier(email);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var match = _state.Accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Email) &&
                string.Equals(NormalizeIdentifier(a.Email), normalized, StringComparison.OrdinalIgnoreCase));
            return match?.IsEmailVerified == true;
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
        const string genericInvalidCredentialsMsg = "E-mail, usuário ou senha inválidos.";

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
            return new CloudVerifyLoginResponseDto(false, false, null, genericInvalidCredentialsMsg);
        }

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var now = DateTimeOffset.UtcNow;

            var match = _state.Accounts.FirstOrDefault(a =>
                string.Equals(NormalizeIdentifier(a.Email), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeIdentifier(a.Username), normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null || string.IsNullOrWhiteSpace(match.PasswordHash))
            {
                // Executa PBKDF2 dummy para evitar enumeração por tempo de resposta e retorna mensagem genérica
                _ = _passwordHasher.VerifyPassword(request.Password, _dummyPasswordHash);
                return new CloudVerifyLoginResponseDto(
                    Verified: false,
                    AccountExists: false,
                    Account: null,
                    Message: genericInvalidCredentialsMsg);
            }

            if (match.LockoutEndsAt.HasValue && match.LockoutEndsAt.Value > now)
            {
                return new CloudVerifyLoginResponseDto(
                    Verified: false,
                    AccountExists: false,
                    Account: null,
                    Message: "Muitas tentativas incorretas. Aguarde alguns minutos e tente novamente.");
            }

            if (!_passwordHasher.VerifyPassword(request.Password, match.PasswordHash))
            {
                match.FailedLoginAttempts++;
                if (match.FailedLoginAttempts >= MaxFailedLoginAttempts)
                {
                    match.LockoutEndsAt = now.Add(LoginLockoutDuration);
                }
                SaveToDiskUnsafe();

                return new CloudVerifyLoginResponseDto(
                    Verified: false,
                    AccountExists: false,
                    Account: null,
                    Message: genericInvalidCredentialsMsg);
            }

            match.FailedLoginAttempts = 0;
            match.LockoutEndsAt = null;
            var (sessionToken, sessionExpiresAt) = IssueSessionTokenUnsafe(match, now);
            EvaluateEntryGraceUnsafe(match, now);
            SaveToDiskUnsafe();

            var effectiveTier = ResolveEffectivePlan(match.PlanTier, match.PlanExpiresAt, now);
            return new CloudVerifyLoginResponseDto(
                Verified: true,
                AccountExists: true,
                Account: new CloudAccountCredentialSyncDto(
                    match.FullName,
                    match.Username,
                    match.Email,
                    string.Empty, // NUNCA devolve PasswordHash pela rede (previne pass-the-hash)
                    match.Phone,
                    match.StoreName,
                    match.StoreSlug,
                    PlanTier: effectiveTier,
                    PlanExpiresAt: effectiveTier == StoreSettings.PlanTierBasic ? null : match.PlanExpiresAt,
                    IsSiteActive: match.IsSiteActive ?? true,
                    IsCourtesyVip: ResolveIsCourtesyVip(match),
                    PlanRevokedAt: match.PlanRevokedAt,
                    IsRecurringSubscription: match.IsRecurringSubscription,
                    BillingGraceEndsAt: match.BillingGraceEndsAt,
                    IsBillingSuspended: match.IsBillingSuspended,
                    BillingWarningMessage: match.BillingWarningMessage,
                    CloudSessionToken: sessionToken,
                    IsEmailVerified: match.IsEmailVerified),
                Message: "Conta verificada com sucesso.",
                CloudSessionToken: sessionToken,
                CloudSessionExpiresAt: sessionExpiresAt,
                IsEmailVerified: match.IsEmailVerified);
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

    public Task<CloudAccountSummary?> ApplyPlanAsync(
        string? targetEmail,
        string? targetUsername,
        string? storeName,
        string planTier,
        int? durationDays,
        bool extendExistingDuration,
        bool activateSite,
        CancellationToken ct = default,
        bool isCourtesyVip = false,
        bool isRecurringSubscription = false) =>
        ApplyPlanCoreAsync(
            targetEmail,
            targetUsername,
            storeName,
            storeSlug: null,
            planTier,
            durationDays,
            explicitExpiresAt: null,
            extendExistingDuration,
            activateSite,
            createIfMissing: false,
            isCourtesyVip,
            isRecurringSubscription,
            ct);

    /// <summary>
    /// Aplica o plano VIP em uma conta existente na nuvem ou cria um registro de direito VIP pré-provisionado
    /// quando acionado por requisição administrativa autenticada (X-YouLink-Sync-Key).
    /// </summary>
    public Task<CloudAccountSummary?> ApplyOrCreatePlanEntitlementAsync(
        string? targetEmail,
        string? targetUsername,
        string? storeName,
        string? storeSlug,
        string planTier,
        int? durationDays,
        bool extendExistingDuration,
        bool activateSite,
        CancellationToken ct = default,
        bool isCourtesyVip = false,
        bool isRecurringSubscription = false,
        DateTimeOffset? explicitExpiresAt = null) =>
        ApplyPlanCoreAsync(
            targetEmail,
            targetUsername,
            storeName,
            storeSlug,
            planTier,
            durationDays,
            explicitExpiresAt,
            extendExistingDuration,
            activateSite,
            createIfMissing: true,
            isCourtesyVip,
            isRecurringSubscription,
            ct);

    private async Task<CloudAccountSummary?> ApplyPlanCoreAsync(
        string? targetEmail,
        string? targetUsername,
        string? storeName,
        string? storeSlug,
        string planTier,
        int? durationDays,
        DateTimeOffset? explicitExpiresAt,
        bool extendExistingDuration,
        bool activateSite,
        bool createIfMissing,
        bool isCourtesyVip,
        bool isRecurringSubscription,
        CancellationToken ct)
    {
        var normalizedTier = StoreSettings.NormalizePlanTier(planTier);

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var match = FindMatchingEntryUnsafe(targetEmail, targetUsername, storeName, storeSlug);
            if (match is null)
            {
                if (!createIfMissing)
                    return null;

                var cleanEmail = NormalizeIdentifier(targetEmail);
                var cleanUsername = NormalizeIdentifier(targetUsername);
                if (cleanUsername == "-")
                    cleanUsername = null;
                var cleanStoreName = !IsGenericStoreIdentifier(storeName) ? TrimOrNull(storeName) : null;
                var cleanStoreSlug = !IsGenericStoreIdentifier(storeSlug) ? TrimOrNull(storeSlug) : null;

                if (string.IsNullOrWhiteSpace(cleanEmail) &&
                    string.IsNullOrWhiteSpace(cleanUsername) &&
                    string.IsNullOrWhiteSpace(cleanStoreName) &&
                    string.IsNullOrWhiteSpace(cleanStoreSlug))
                {
                    return null;
                }

                match = new CloudAccountEntry
                {
                    Email = cleanEmail ?? string.Empty,
                    Username = cleanUsername ?? (!string.IsNullOrWhiteSpace(cleanEmail) ? User.DeriveDefaultUsername(cleanEmail) : string.Empty),
                    FullName = cleanStoreName ?? cleanUsername ?? cleanEmail ?? "Cliente YouLink",
                    PasswordHash = string.Empty,
                    StoreName = cleanStoreName,
                    StoreSlug = cleanStoreSlug ?? (!string.IsNullOrWhiteSpace(cleanStoreName) ? Category.GenerateSlug(cleanStoreName) : null),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _state.Accounts.Add(match);
            }

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
                DateTimeOffset? nextExpiry = explicitExpiresAt;
                if (!nextExpiry.HasValue && durationDays.HasValue && durationDays.Value > 0)
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

            if (!string.IsNullOrWhiteSpace(targetEmail) && string.IsNullOrWhiteSpace(match.Email))
                match.Email = NormalizeIdentifier(targetEmail) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(targetUsername) && targetUsername.Trim() != "-" && string.IsNullOrWhiteSpace(match.Username))
                match.Username = NormalizeIdentifier(targetUsername) ?? string.Empty;
            if (!IsGenericStoreIdentifier(storeName) && (string.IsNullOrWhiteSpace(match.StoreName) || IsGenericStoreIdentifier(match.StoreName)))
                match.StoreName = storeName!.Trim();
            if (!IsGenericStoreIdentifier(storeSlug) && (string.IsNullOrWhiteSpace(match.StoreSlug) || IsGenericStoreIdentifier(match.StoreSlug)))
                match.StoreSlug = storeSlug!.Trim();

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
        var meta = await GetPlanStatusWithAccountAsync(email, username, storeSlug, storeName, ct);
        return meta?.Status;
    }

    public async Task<(CloudPlanStatusDto Status, CloudAccountSummary Account)?> GetPlanStatusWithAccountAsync(
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
            var dto = new CloudPlanStatusDto(
                PlanTier: effectiveTier,
                PlanExpiresAt: effectiveTier == StoreSettings.PlanTierBasic ? null : match.PlanExpiresAt,
                IsSiteActive: match.IsSiteActive ?? true,
                StoreName: match.StoreName,
                StoreSlug: match.StoreSlug,
                IsCourtesyVip: ResolveIsCourtesyVip(match),
                PlanRevokedAt: match.PlanRevokedAt,
                IsRecurringSubscription: match.IsRecurringSubscription,
                BillingGraceEndsAt: match.BillingGraceEndsAt,
                IsBillingSuspended: match.IsBillingSuspended,
                BillingWarningMessage: match.BillingWarningMessage);

            return (dto, MapToSummary(match));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RecordClientCheckInAsync(
        string? email,
        string? username,
        string? storeSlug,
        string? storeName,
        string? clientPlanTier,
        DateTimeOffset? clientExpiresAt = null,
        string? clientAppVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientPlanTier) && string.IsNullOrWhiteSpace(clientAppVersion))
            return false;

        await _lock.WaitAsync(ct);
        try
        {
            RefreshFromDiskIfModifiedUnsafe();
            var match = FindMatchingEntryUnsafe(email, username, storeName, storeSlug);
            if (match is null)
                return false;

            var now = DateTimeOffset.UtcNow;
            match.LastClientCheckInAt = now;
            if (!string.IsNullOrWhiteSpace(clientPlanTier))
                match.LastReportedClientPlanTier = StoreSettings.NormalizePlanTier(clientPlanTier);
            if (clientExpiresAt.HasValue)
                match.LastReportedClientPlanExpiresAt = clientExpiresAt;
            if (!string.IsNullOrWhiteSpace(clientAppVersion))
                match.LastClientAppVersion = clientAppVersion.Trim();
            match.UpdatedAt = now;

            SaveToDiskUnsafe();
            return true;
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
                !string.IsNullOrWhiteSpace(a.Email) &&
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

        var trimmedSlug = !IsGenericStoreIdentifier(storeSlug) ? TrimOrNull(storeSlug) : null;
        if (!string.IsNullOrWhiteSpace(trimmedSlug))
        {
            var generatedSlug = Category.GenerateSlug(trimmedSlug);
            var compactSlug = CompressSlug(generatedSlug);
            var bySlug = _state.Accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.StoreSlug) &&
                !IsGenericStoreIdentifier(a.StoreSlug) &&
                (string.Equals(a.StoreSlug.Trim(), trimmedSlug, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Category.GenerateSlug(a.StoreSlug), generatedSlug, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(compactSlug) && string.Equals(CompressSlug(Category.GenerateSlug(a.StoreSlug)), compactSlug, StringComparison.OrdinalIgnoreCase))));
            if (bySlug is not null)
                return bySlug;
        }

        var trimmedStore = !IsGenericStoreIdentifier(storeName) ? TrimOrNull(storeName) : null;
        if (!string.IsNullOrWhiteSpace(trimmedStore))
        {
            var generatedStoreSlug = Category.GenerateSlug(trimmedStore);
            var compactStoreSlug = CompressSlug(generatedStoreSlug);
            var byStore = _state.Accounts.FirstOrDefault(a =>
                (!string.IsNullOrWhiteSpace(a.StoreName) &&
                 !IsGenericStoreIdentifier(a.StoreName) &&
                 string.Equals(a.StoreName.Trim(), trimmedStore, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(a.StoreSlug) &&
                 !IsGenericStoreIdentifier(a.StoreSlug) &&
                 (string.Equals(Category.GenerateSlug(a.StoreSlug), generatedStoreSlug, StringComparison.OrdinalIgnoreCase) ||
                  (!string.IsNullOrWhiteSpace(compactStoreSlug) && string.Equals(CompressSlug(Category.GenerateSlug(a.StoreSlug)), compactStoreSlug, StringComparison.OrdinalIgnoreCase)))));
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
            ResolveIsCourtesyVip(a),
            a.PlanRevokedAt,
            a.IsRecurringSubscription,
            a.BillingGraceEndsAt,
            a.IsBillingSuspended,
            a.BillingWarningMessage,
            a.LastClientCheckInAt,
            a.LastReportedClientPlanTier,
            a.LastClientAppVersion,
            HasPassword: !string.IsNullOrWhiteSpace(a.PasswordHash),
            IsEmailVerified: a.IsEmailVerified);

    private static bool ResolveIsCourtesyVip(CloudAccountEntry entry)
    {
        var tier = StoreSettings.NormalizePlanTier(entry.PlanTier);
        if (string.Equals(tier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(entry.PlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase))
            return true;

        // Todo VIP Vitalício (sem vencimento e sem assinatura recorrente) é cortesia concedida pelo Admin.
        if (!entry.PlanExpiresAt.HasValue && !entry.IsRecurringSubscription)
            return true;

        // Contas com VIP anterior à v1.0.46 sem registro explícito de pagamento permanecem como cortesia.
        if (string.IsNullOrWhiteSpace(entry.PlanBillingKind) && !entry.IsRecurringSubscription)
            return true;

        return false;
    }

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

    internal static bool IsGenericStoreIdentifier(string? value)
    {
        var trimmed = TrimOrNull(value);
        return trimmed is null ||
               string.Equals(trimmed, "Minha Loja", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "minha-loja", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "minhaloja", StringComparison.OrdinalIgnoreCase);
    }

    internal static string CompressSlug(string? slug) =>
        string.IsNullOrWhiteSpace(slug)
            ? string.Empty
            : new string(slug.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static (string RawToken, DateTimeOffset ExpiresAt) IssueSessionTokenUnsafe(
        CloudAccountEntry account,
        DateTimeOffset now)
    {
        account.Sessions ??= [];
        account.Sessions.RemoveAll(s => s.ExpiresAt <= now || string.IsNullOrWhiteSpace(s.TokenHash));
        if (account.Sessions.Count >= 8)
        {
            account.Sessions = account.Sessions.OrderByDescending(s => s.ExpiresAt).Take(7).ToList();
        }

        var rawToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = now.Add(SessionTokenLifetime);
        account.Sessions.Add(new CloudAccountSessionEntry
        {
            TokenHash = HashSessionTokenSha256(rawToken),
            ExpiresAt = expiresAt
        });
        return (rawToken, expiresAt);
    }

    private static bool VerifyAccountSessionTokenUnsafe(
        CloudAccountEntry account,
        string? candidateRawToken,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(candidateRawToken) || account.Sessions is null || account.Sessions.Count == 0)
            return false;

        var candidateBytes = Encoding.ASCII.GetBytes(HashSessionTokenSha256(candidateRawToken.Trim()));
        foreach (var session in account.Sessions)
        {
            if (session.ExpiresAt <= now || string.IsNullOrWhiteSpace(session.TokenHash))
                continue;

            var storedBytes = Encoding.ASCII.GetBytes(session.TokenHash);
            if (storedBytes.Length == candidateBytes.Length &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedBytes, candidateBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static string HashSessionTokenSha256(string rawToken)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class CloudAccountsRegistryState
    {
        public List<CloudAccountEntry> Accounts { get; set; } = [];
    }

    private sealed class CloudAccountSessionEntry
    {
        public string TokenHash { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
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
        public DateTimeOffset? LastClientCheckInAt { get; set; }
        public string? LastReportedClientPlanTier { get; set; }
        public DateTimeOffset? LastReportedClientPlanExpiresAt { get; set; }
        public string? LastClientAppVersion { get; set; }
        public int FailedLoginAttempts { get; set; }
        public DateTimeOffset? LockoutEndsAt { get; set; }
        public bool IsEmailVerified { get; set; }
        public DateTimeOffset? EmailVerifiedAt { get; set; }
        public List<CloudAccountSessionEntry> Sessions { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed record CloudAccountUpsertResult(
    bool Success,
    int StatusCode,
    string Message,
    string? CloudSessionToken = null,
    DateTimeOffset? CloudSessionExpiresAt = null,
    bool RequiresEmailVerification = false);

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
    string? BillingWarningMessage = null,
    DateTimeOffset? LastClientCheckInAt = null,
    string? LastReportedClientPlanTier = null,
    string? LastClientAppVersion = null,
    bool HasPassword = true,
    bool IsEmailVerified = false);
