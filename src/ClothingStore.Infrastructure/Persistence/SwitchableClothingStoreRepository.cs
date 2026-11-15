using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Exceptions;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Repositório multi-loja local com isolamento completo por usuário/conta.
/// Cada nova conta criada no aplicativo recebe seu próprio arquivo de loja independente
/// (produtos, estoque, plano, aparência e configurações), enquanto o login alterna automaticamente
/// para o workspace pertencente à conta autenticada.
/// </summary>
public sealed class SwitchableClothingStoreRepository : IClothingStoreRepository
{
    public const string DefaultWorkspaceId = "default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IPasswordHasher _passwordHasher;
    private readonly string _defaultStorageFilePath;
    private readonly string _baseDirectory;
    private readonly string _workspacesDirectory;
    private readonly string _indexFilePath;
    private readonly bool _enablePlatformOwnerCompanion;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ClothingStoreRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);

    private string _activeWorkspaceId = DefaultWorkspaceId;
    private WorkspaceIndexState _index;

    public string ActiveWorkspaceId => _activeWorkspaceId;

    public bool IsDefaultWorkspace =>
        string.Equals(_activeWorkspaceId, DefaultWorkspaceId, StringComparison.OrdinalIgnoreCase);

    public string ActiveStorageFilePath => GetOrCreateRepository(_activeWorkspaceId).StorageFilePath;

    public string DefaultStorageFilePath => _defaultStorageFilePath;

    public event Action<string>? WorkspaceChanged;

    public sealed record ProvisionedWorkspaceHandle(
        string WorkspaceId,
        bool CreatedNewFile,
        string PreviousWorkspaceId);

    public SwitchableClothingStoreRepository(
        IPasswordHasher passwordHasher,
        string defaultStorageFilePath,
        bool enablePlatformOwnerCompanion = true)
    {
        ArgumentNullException.ThrowIfNull(passwordHasher);
        if (string.IsNullOrWhiteSpace(defaultStorageFilePath))
            throw new ArgumentException("O caminho padrão do banco de dados é obrigatório.", nameof(defaultStorageFilePath));

        _passwordHasher = passwordHasher;
        _defaultStorageFilePath = Path.GetFullPath(defaultStorageFilePath);
        _baseDirectory = Path.GetDirectoryName(_defaultStorageFilePath) ?? AppContext.BaseDirectory;
        _workspacesDirectory = Path.Combine(_baseDirectory, "store_workspaces");
        _indexFilePath = Path.Combine(_baseDirectory, "account_workspaces.json");
        _enablePlatformOwnerCompanion = enablePlatformOwnerCompanion;
        _index = LoadIndexUnsafe();

        GetOrCreateRepository(DefaultWorkspaceId);
    }

    private ClothingStoreRepository ActiveRepository => GetOrCreateRepository(_activeWorkspaceId);

    public string ResolveScopedPreferenceKey(string baseKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseKey);
        return IsDefaultWorkspace ? baseKey : $"{baseKey}__{_activeWorkspaceId}";
    }

    public async Task<ProvisionedWorkspaceHandle> ProvisionIsolatedWorkspaceForNewAccountAsync(
        string email,
        string? username,
        CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeIdentifier(email);
        var normalizedUsername = NormalizeIdentifier(username);

        if (string.IsNullOrWhiteSpace(normalizedEmail))
            throw new DomainException("Informe um e-mail ou login de acesso válido.");

        await _switchLock.WaitAsync(ct);
        try
        {
            var previousWorkspaceId = _activeWorkspaceId;

            if (await IdentifierExistsInAnyWorkspaceAsync(normalizedEmail, ct))
            {
                throw new DomainException("Este e-mail/login já está cadastrado em uma conta neste dispositivo. Use a aba 'Entrar'.");
            }

            if (!string.IsNullOrWhiteSpace(normalizedUsername) &&
                await IdentifierExistsInAnyWorkspaceAsync(normalizedUsername, ct))
            {
                throw new DomainException("Este nome de usuário já está cadastrado em uma conta neste dispositivo. Use a aba 'Entrar'.");
            }

            var defaultRepo = GetOrCreateRepository(DefaultWorkspaceId);
            var existingEmployees = await defaultRepo.GetEmployeesAsync(ct);
            var existingProducts = await defaultRepo.GetProductsAsync(includeArchived: true, ct);
            var existingCategories = await defaultRepo.GetCategoriesAsync(onlyActive: false, ct);
            var existingOrders = await defaultRepo.GetOrdersAsync(ct);

            var canUseDefaultWorkspace =
                existingEmployees.Count == 0 &&
                existingProducts.Count == 0 &&
                existingCategories.Count == 0 &&
                existingOrders.Count == 0;

            string targetWorkspaceId;
            bool createdNewFile;

            if (canUseDefaultWorkspace)
            {
                targetWorkspaceId = DefaultWorkspaceId;
                createdNewFile = false;
            }
            else
            {
                Directory.CreateDirectory(_workspacesDirectory);
                targetWorkspaceId = $"ws_{Guid.NewGuid():N}"[..19];
                createdNewFile = true;
            }

            GetOrCreateRepository(targetWorkspaceId);
            UpsertIndexUnsafe(targetWorkspaceId, normalizedEmail, normalizedUsername);
            SetActiveWorkspaceInternal(targetWorkspaceId);

            return new ProvisionedWorkspaceHandle(targetWorkspaceId, createdNewFile, previousWorkspaceId);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async Task RollbackProvisionedWorkspaceAsync(
        ProvisionedWorkspaceHandle handle,
        string? email,
        string? username,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await _switchLock.WaitAsync(ct);
        try
        {
            RemoveFromIndexUnsafe(NormalizeIdentifier(email), NormalizeIdentifier(username));

            if (handle.CreatedNewFile &&
                !string.Equals(handle.WorkspaceId, DefaultWorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                _repositories.TryRemove(handle.WorkspaceId, out _);
                var dbPath = ResolveWorkspacePath(handle.WorkspaceId);
                TryDeleteFileQuietly(dbPath);
            }

            SetActiveWorkspaceInternal(
                string.IsNullOrWhiteSpace(handle.PreviousWorkspaceId)
                    ? DefaultWorkspaceId
                    : handle.PreviousWorkspaceId);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async Task<string?> SwitchToWorkspaceForAccountAsync(string? loginOrEmail, CancellationToken ct = default)
    {
        var normalized = NormalizeIdentifier(loginOrEmail);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        await _switchLock.WaitAsync(ct);
        try
        {
            _index = LoadIndexUnsafe();
            if (_index.AccountToWorkspace.TryGetValue(normalized, out var indexedWorkspaceId) &&
                WorkspaceFileExists(indexedWorkspaceId))
            {
                var indexedRepo = GetOrCreateRepository(indexedWorkspaceId);
                if (await indexedRepo.GetUserByEmailAsync(normalized, ct) is not null)
                {
                    SetActiveWorkspaceInternal(indexedWorkspaceId);
                    return indexedWorkspaceId;
                }
            }

            foreach (var workspaceId in EnumerateCandidateWorkspaceIds())
            {
                var repo = GetOrCreateRepository(workspaceId);
                var user = await repo.GetUserByEmailAsync(normalized, ct);
                if (user is not null)
                {
                    UpsertIndexUnsafe(workspaceId, user.Email, user.Username);
                    SetActiveWorkspaceInternal(workspaceId);
                    return workspaceId;
                }
            }

            return null;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async Task<string> SwitchToWorkspaceByIdAsync(
        string? workspaceId,
        Guid? userId = null,
        string? loginOrEmail = null,
        CancellationToken ct = default)
    {
        await _switchLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(workspaceId) && WorkspaceFileExists(workspaceId))
            {
                var repo = GetOrCreateRepository(workspaceId);
                var matchesUser = !userId.HasValue || await repo.GetUserByIdAsync(userId.Value, ct) is not null;
                if (matchesUser)
                {
                    SetActiveWorkspaceInternal(workspaceId);
                    return workspaceId;
                }
            }

            var normalizedLogin = NormalizeIdentifier(loginOrEmail);
            foreach (var candidateId in EnumerateCandidateWorkspaceIds())
            {
                var repo = GetOrCreateRepository(candidateId);
                var byId = userId.HasValue ? await repo.GetUserByIdAsync(userId.Value, ct) : null;
                var byEmail = !string.IsNullOrWhiteSpace(normalizedLogin)
                    ? await repo.GetUserByEmailAsync(normalizedLogin, ct)
                    : null;

                var matched = byId ?? byEmail;
                if (matched is not null)
                {
                    UpsertIndexUnsafe(candidateId, matched.Email, matched.Username);
                    SetActiveWorkspaceInternal(candidateId);
                    return candidateId;
                }
            }

            SetActiveWorkspaceInternal(DefaultWorkspaceId);
            return DefaultWorkspaceId;
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async Task IndexAccountAsync(string workspaceId, string? email, string? username, CancellationToken ct = default)
    {
        var cleanWorkspace = string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId.Trim();
        await _switchLock.WaitAsync(ct);
        try
        {
            UpsertIndexUnsafe(cleanWorkspace, NormalizeIdentifier(email), NormalizeIdentifier(username));
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public void ResetToDefaultWorkspace()
    {
        _switchLock.Wait();
        try
        {
            SetActiveWorkspaceInternal(DefaultWorkspaceId);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private async Task<bool> IdentifierExistsInAnyWorkspaceAsync(string normalizedIdentifier, CancellationToken ct)
    {
        foreach (var workspaceId in EnumerateCandidateWorkspaceIds())
        {
            var repo = GetOrCreateRepository(workspaceId);
            if (await repo.GetUserByEmailAsync(normalizedIdentifier, ct) is not null)
                return true;
        }

        return false;
    }

    private List<string> EnumerateCandidateWorkspaceIds()
    {
        var ordered = new List<string> { DefaultWorkspaceId };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultWorkspaceId };

        foreach (var wsId in _index.AccountToWorkspace.Values)
        {
            if (!string.IsNullOrWhiteSpace(wsId) && WorkspaceFileExists(wsId) && seen.Add(wsId))
            {
                ordered.Add(wsId);
            }
        }

        if (Directory.Exists(_workspacesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(_workspacesDirectory, "*.json"))
            {
                var wsId = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(wsId) && seen.Add(wsId))
                {
                    ordered.Add(wsId);
                }
            }
        }

        return ordered;
    }

    private void SetActiveWorkspaceInternal(string workspaceId)
    {
        var normalizedId = string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId.Trim();
        var changed = !string.Equals(_activeWorkspaceId, normalizedId, StringComparison.OrdinalIgnoreCase);
        _activeWorkspaceId = normalizedId;
        GetOrCreateRepository(normalizedId);

        if (changed)
        {
            WorkspaceChanged?.Invoke(normalizedId);
        }
    }

    private ClothingStoreRepository GetOrCreateRepository(string workspaceId)
    {
        var cleanId = string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId.Trim();
        return _repositories.GetOrAdd(cleanId, id =>
        {
            var isDefault = string.Equals(id, DefaultWorkspaceId, StringComparison.OrdinalIgnoreCase);
            var dbPath = ResolveWorkspacePath(id);
            return new ClothingStoreRepository(
                _passwordHasher,
                dbPath,
                enablePlatformOwnerCompanion: isDefault && _enablePlatformOwnerCompanion);
        });
    }

    private string ResolveWorkspacePath(string workspaceId)
    {
        if (string.Equals(workspaceId, DefaultWorkspaceId, StringComparison.OrdinalIgnoreCase))
            return _defaultStorageFilePath;

        var safeFileName = SanitizeWorkspaceId(workspaceId);
        return Path.Combine(_workspacesDirectory, $"{safeFileName}.json");
    }

    private bool WorkspaceFileExists(string workspaceId)
    {
        if (string.Equals(workspaceId, DefaultWorkspaceId, StringComparison.OrdinalIgnoreCase))
            return File.Exists(_defaultStorageFilePath);

        return File.Exists(ResolveWorkspacePath(workspaceId));
    }

    private static string SanitizeWorkspaceId(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                builder.Append(ch);
        }

        var result = builder.ToString();
        return string.IsNullOrWhiteSpace(result) ? "ws_store" : result;
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().TrimStart('@').ToLowerInvariant();
    }

    private WorkspaceIndexState LoadIndexUnsafe()
    {
        try
        {
            if (File.Exists(_indexFilePath))
            {
                var json = File.ReadAllText(_indexFilePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<WorkspaceIndexState>(json, JsonOptions);
                if (parsed is not null)
                {
                    parsed.AccountToWorkspace = new Dictionary<string, string>(
                        parsed.AccountToWorkspace ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
                    return parsed;
                }
            }
        }
        catch
        {
        }

        return new WorkspaceIndexState();
    }

    private void UpsertIndexUnsafe(string workspaceId, string? email, string? username)
    {
        var normEmail = NormalizeIdentifier(email);
        var normUser = NormalizeIdentifier(username);
        var changed = false;

        if (!string.IsNullOrWhiteSpace(normEmail))
        {
            _index.AccountToWorkspace[normEmail] = workspaceId;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(normUser))
        {
            _index.AccountToWorkspace[normUser] = workspaceId;
            changed = true;
        }

        if (changed)
            SaveIndexUnsafe();
    }

    private void RemoveFromIndexUnsafe(string? email, string? username)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(email))
            changed |= _index.AccountToWorkspace.Remove(email);
        if (!string.IsNullOrWhiteSpace(username))
            changed |= _index.AccountToWorkspace.Remove(username);

        if (changed)
            SaveIndexUnsafe();
    }

    private void SaveIndexUnsafe()
    {
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            var json = JsonSerializer.Serialize(_index, JsonOptions);
            var tempPath = $"{_indexFilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _indexFilePath, overwrite: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class WorkspaceIndexState
    {
        public Dictionary<string, string> AccountToWorkspace { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ---------------- Delegação IClothingStoreRepository ----------------

    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default) =>
        ActiveRepository.GetRolesAsync(ct);

    public Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default) =>
        ActiveRepository.GetRoleByNameAsync(roleName, ct);

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default) =>
        ActiveRepository.GetUserByEmailAsync(email, ct);

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct = default) =>
        ActiveRepository.GetUserByIdAsync(userId, ct);

    public Task<Employee?> GetEmployeeByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        ActiveRepository.GetEmployeeByUserIdAsync(userId, ct);

    public Task<Employee?> GetEmployeeByIdAsync(Guid employeeId, CancellationToken ct = default) =>
        ActiveRepository.GetEmployeeByIdAsync(employeeId, ct);

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken ct = default) =>
        ActiveRepository.GetEmployeesAsync(ct);

    public Task AddEmployeeWithUserAsync(User user, Employee employee, CancellationToken ct = default) =>
        ActiveRepository.AddEmployeeWithUserAsync(user, employee, ct);

    public Task UpdateEmployeeAsync(Employee employee, User? user = null, CancellationToken ct = default) =>
        ActiveRepository.UpdateEmployeeAsync(employee, user, ct);

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(bool onlyActive = false, CancellationToken ct = default) =>
        ActiveRepository.GetCategoriesAsync(onlyActive, ct);

    public Task<Category?> GetCategoryByIdAsync(Guid id, CancellationToken ct = default) =>
        ActiveRepository.GetCategoryByIdAsync(id, ct);

    public Task<Category?> GetCategoryBySlugAsync(string slug, CancellationToken ct = default) =>
        ActiveRepository.GetCategoryBySlugAsync(slug, ct);

    public Task AddCategoryAsync(Category category, CancellationToken ct = default) =>
        ActiveRepository.AddCategoryAsync(category, ct);

    public Task UpdateCategoryAsync(Category category, CancellationToken ct = default) =>
        ActiveRepository.UpdateCategoryAsync(category, ct);

    public Task<IReadOnlyList<Product>> GetProductsAsync(bool includeArchived = false, CancellationToken ct = default) =>
        ActiveRepository.GetProductsAsync(includeArchived, ct);

    public Task<Product?> GetProductByIdAsync(Guid id, CancellationToken ct = default) =>
        ActiveRepository.GetProductByIdAsync(id, ct);

    public Task<Product?> GetProductBySlugOrIdAsync(string slugOrId, CancellationToken ct = default) =>
        ActiveRepository.GetProductBySlugOrIdAsync(slugOrId, ct);

    public Task<ProductVariant?> GetVariantByIdAsync(Guid variantId, CancellationToken ct = default) =>
        ActiveRepository.GetVariantByIdAsync(variantId, ct);

    public Task AddProductAsync(Product product, CancellationToken ct = default) =>
        ActiveRepository.AddProductAsync(product, ct);

    public Task UpdateProductAsync(Product product, CancellationToken ct = default) =>
        ActiveRepository.UpdateProductAsync(product, ct);

    public Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default) =>
        ActiveRepository.DeleteOrArchiveProductAsync(productId, ct);

    public Task<IReadOnlyList<StockMovement>> GetStockMovementsAsync(
        Guid? productId = null,
        Guid? variantId = null,
        Guid? employeeId = null,
        int limit = 100,
        CancellationToken ct = default) =>
        ActiveRepository.GetStockMovementsAsync(productId, variantId, employeeId, limit, ct);

    public Task RecordStockMovementAsync(ProductVariant variant, StockMovement movement, CancellationToken ct = default) =>
        ActiveRepository.RecordStockMovementAsync(variant, movement, ct);

    public Task<StoreSettings> GetStoreSettingsAsync(CancellationToken ct = default) =>
        ActiveRepository.GetStoreSettingsAsync(ct);

    public Task UpdateStoreSettingsAsync(StoreSettings settings, CancellationToken ct = default) =>
        ActiveRepository.UpdateStoreSettingsAsync(settings, ct);

    public async Task<IReadOnlyList<PlanActivationCode>> GetPlanActivationCodesAsync(CancellationToken ct = default)
    {
        var merged = new Dictionary<string, PlanActivationCode>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in EnumerateGiftCardRepositories())
        {
            var list = await repo.GetPlanActivationCodesAsync(ct);
            foreach (var item in list)
            {
                if (!merged.TryGetValue(item.Code, out var existing) || IsMoreRecentGiftCardState(item, existing))
                {
                    merged[item.Code] = item;
                }
            }
        }

        return merged.Values
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
    }

    public async Task<PlanActivationCode?> GetPlanActivationCodeByCodeAsync(string code, CancellationToken ct = default)
    {
        PlanActivationCode? bestMatch = null;
        foreach (var repo in EnumerateGiftCardRepositories())
        {
            var candidate = await repo.GetPlanActivationCodeByCodeAsync(code, ct);
            if (candidate is null)
                continue;

            if (bestMatch is null || IsMoreRecentGiftCardState(candidate, bestMatch))
            {
                bestMatch = candidate;
            }
        }

        return bestMatch;
    }

    public async Task<PlanActivationCode?> GetPlanActivationCodeByIdAsync(Guid id, CancellationToken ct = default)
    {
        PlanActivationCode? bestMatch = null;
        foreach (var repo in EnumerateGiftCardRepositories())
        {
            var candidate = await repo.GetPlanActivationCodeByIdAsync(id, ct);
            if (candidate is null)
                continue;

            if (bestMatch is null || IsMoreRecentGiftCardState(candidate, bestMatch))
            {
                bestMatch = candidate;
            }
        }

        return bestMatch;
    }

    public async Task SavePlanActivationCodeAsync(PlanActivationCode activationCode, CancellationToken ct = default)
    {
        var activeRepo = ActiveRepository;
        await activeRepo.SavePlanActivationCodeAsync(activationCode, ct);

        var defaultRepo = GetOrCreateRepository(DefaultWorkspaceId);
        if (!ReferenceEquals(defaultRepo, activeRepo))
        {
            await defaultRepo.SavePlanActivationCodeAsync(activationCode, ct);
        }

        foreach (var repo in EnumerateGiftCardRepositories())
        {
            if (ReferenceEquals(repo, activeRepo) || ReferenceEquals(repo, defaultRepo))
                continue;

            var existingByCode = await repo.GetPlanActivationCodeByCodeAsync(activationCode.Code, ct);
            var existingById = existingByCode ?? await repo.GetPlanActivationCodeByIdAsync(activationCode.Id, ct);
            if (existingById is not null)
            {
                await repo.SavePlanActivationCodeAsync(activationCode, ct);
            }
        }
    }

    private IEnumerable<ClothingStoreRepository> EnumerateGiftCardRepositories()
    {
        var yielded = new HashSet<ClothingStoreRepository>();

        var active = ActiveRepository;
        if (yielded.Add(active))
            yield return active;

        var defaultRepo = GetOrCreateRepository(DefaultWorkspaceId);
        if (yielded.Add(defaultRepo))
            yield return defaultRepo;

        foreach (var wsId in EnumerateCandidateWorkspaceIds())
        {
            var repo = GetOrCreateRepository(wsId);
            if (yielded.Add(repo))
                yield return repo;
        }

        var companionRepo = TryGetWindowsSharedAppDataRepository();
        if (companionRepo is not null && yielded.Add(companionRepo))
            yield return companionRepo;
    }

    private ClothingStoreRepository? _windowsSharedAppDataRepo;

    private ClothingStoreRepository? TryGetWindowsSharedAppDataRepository()
    {
        if (!OperatingSystem.IsWindows() || !_enablePlatformOwnerCompanion)
            return null;

        try
        {
            var fullDefaultPath = Path.GetFullPath(_defaultStorageFilePath);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (fullDefaultPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            var sharedDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YouLinkShared",
                "App_Data",
                "clothingstore_db.json");

            if (!File.Exists(sharedDbPath))
                return null;

            if (string.Equals(
                    Path.GetFullPath(sharedDbPath),
                    fullDefaultPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return _windowsSharedAppDataRepo ??= new ClothingStoreRepository(_passwordHasher, sharedDbPath);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMoreRecentGiftCardState(PlanActivationCode candidate, PlanActivationCode existing)
    {
        if (candidate.IsRedeemed && !existing.IsRedeemed)
            return true;
        if (!candidate.IsRedeemed && existing.IsRedeemed)
            return false;
        if (candidate.IsRevoked != existing.IsRevoked)
        {
            var candidateStamp = candidate.StatusUpdatedAt ?? candidate.CreatedAt;
            var existingStamp = existing.StatusUpdatedAt ?? existing.CreatedAt;
            if (candidateStamp != existingStamp)
                return candidateStamp > existingStamp;
            return candidate.IsRevoked;
        }

        var candTime = candidate.RedeemedAt ?? candidate.StatusUpdatedAt ?? candidate.CreatedAt;
        var existTime = existing.RedeemedAt ?? existing.StatusUpdatedAt ?? existing.CreatedAt;
        return candTime > existTime;
    }

    public Task<IReadOnlyList<StoreOrder>> GetOrdersAsync(CancellationToken ct = default) =>
        ActiveRepository.GetOrdersAsync(ct);

    public Task<StoreOrder?> GetOrderByIdAsync(Guid id, CancellationToken ct = default) =>
        ActiveRepository.GetOrderByIdAsync(id, ct);

    public Task SaveOrderAsync(StoreOrder order, CancellationToken ct = default) =>
        ActiveRepository.SaveOrderAsync(order, ct);

    public Task<IReadOnlyList<PlatformSubscriptionOrder>> GetPlatformSubscriptionOrdersAsync(CancellationToken ct = default) =>
        ActiveRepository.GetPlatformSubscriptionOrdersAsync(ct);

    public Task<PlatformSubscriptionOrder?> GetPlatformSubscriptionOrderByIdAsync(Guid id, CancellationToken ct = default) =>
        ActiveRepository.GetPlatformSubscriptionOrderByIdAsync(id, ct);

    public Task<PlatformSubscriptionOrder?> GetPlatformSubscriptionOrderByNumberAsync(string orderNumber, CancellationToken ct = default) =>
        ActiveRepository.GetPlatformSubscriptionOrderByNumberAsync(orderNumber, ct);

    public Task SavePlatformSubscriptionOrderAsync(PlatformSubscriptionOrder order, CancellationToken ct = default) =>
        ActiveRepository.SavePlatformSubscriptionOrderAsync(order, ct);
}
