using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace ClothingStore.Infrastructure.Persistence;

/// <summary>
/// Helpers compartilhados entre ClothingStore.Web e ClothingStore.Api para aplicação remota de VIP,
/// resolução unificada de /api/cloud/plan-status e verificação/reparo do client de cada usuário.
/// </summary>
public static class CloudPlatformPlanSyncHelper
{
    public sealed record MatchedTenantTarget(
        IClothingStoreRepository Repository,
        bool IsDefaultRepository,
        string? Slug,
        string? OwnerEmail,
        string? EmployeeEmail,
        string? EmployeeUsername);

    public static bool ResolveTenantIsCourtesyVip(StoreSettings settings, bool? cloudIsCourtesy = null)
    {
        var tier = settings.GetEffectivePlanTier();
        if (string.Equals(tier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
            return false;

        if (cloudIsCourtesy == true)
            return true;

        if (string.Equals(settings.ActivePlanBillingKind, PlanActivationCode.BillingKindGiftedUnpaid, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!settings.PlanExpiresAt.HasValue && !settings.IsRecurringSubscription)
            return true;

        return false;
    }

    public static async Task<MatchedTenantTarget?> FindMatchingTenantRepositoryAsync(
        IClothingStoreRepository rootRepository,
        MultiTenantGatewayService gateway,
        string? email,
        string? username,
        string? storeName,
        string? storeSlug,
        CloudAccountSummary? cloudMatch,
        bool allowGenericRootFallback,
        CancellationToken ct)
    {
        var targetStoreName = !CloudAccountRegistryService.IsGenericStoreIdentifier(storeName)
            ? storeName!.Trim()
            : (!CloudAccountRegistryService.IsGenericStoreIdentifier(cloudMatch?.StoreName) ? cloudMatch!.StoreName!.Trim() : null);

        var targetEmail = !string.IsNullOrWhiteSpace(email)
            ? email.Trim()
            : cloudMatch?.Email?.Trim();

        var rawUsername = !string.IsNullOrWhiteSpace(username) ? username.Trim().TrimStart('@') : cloudMatch?.Username?.Trim();
        var targetUsername = string.IsNullOrWhiteSpace(rawUsername) || rawUsername == "-" ? null : rawUsername;

        var rawSlug = !CloudAccountRegistryService.IsGenericStoreIdentifier(storeSlug)
            ? storeSlug!.Trim()
            : (!CloudAccountRegistryService.IsGenericStoreIdentifier(cloudMatch?.StoreSlug) ? cloudMatch!.StoreSlug!.Trim() : targetStoreName);

        var normalizedSlug = string.IsNullOrWhiteSpace(rawSlug)
            ? null
            : gateway.StoreResolver.NormalizeStoreSlug(rawSlug);
        var compactSlug = CloudAccountRegistryService.CompressSlug(normalizedSlug ?? rawSlug);

        if (!string.IsNullOrWhiteSpace(normalizedSlug))
        {
            var directRepo = await gateway.StoreResolver.ResolveReadRepositoryAsync(normalizedSlug, ct);
            if (directRepo is not null && !ReferenceEquals(directRepo, rootRepository))
            {
                var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(normalizedSlug, ct);
                return new MatchedTenantTarget(directRepo, false, normalizedSlug, creds?.OwnerEmail, null, null);
            }
        }

        foreach (var slug in await gateway.StoreResolver.ListTenantSlugsAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(slug) || string.Equals(slug, "__root__", StringComparison.OrdinalIgnoreCase))
                continue;

            var repo = await gateway.StoreResolver.ResolveReadRepositoryAsync(slug, ct);
            if (repo is null || ReferenceEquals(repo, rootRepository))
                continue;

            var settings = await repo.GetStoreSettingsAsync(ct);
            var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(slug, ct);

            var emailMatches = !string.IsNullOrWhiteSpace(targetEmail) &&
                               !string.IsNullOrWhiteSpace(creds?.OwnerEmail) &&
                               string.Equals(creds.OwnerEmail.Trim(), targetEmail, StringComparison.OrdinalIgnoreCase);

            var nameMatches = !string.IsNullOrWhiteSpace(targetStoreName) &&
                              (string.Equals(settings.StoreName.Trim(), targetStoreName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(slug, normalizedSlug, StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrWhiteSpace(compactSlug) && string.Equals(CloudAccountRegistryService.CompressSlug(slug), compactSlug, StringComparison.OrdinalIgnoreCase)));

            if (emailMatches || nameMatches)
                return new MatchedTenantTarget(repo, false, slug, creds?.OwnerEmail, null, null);

            if (!string.IsNullOrWhiteSpace(targetUsername) || !string.IsNullOrWhiteSpace(targetEmail))
            {
                var emps = await repo.GetEmployeesAsync(ct);
                foreach (var emp in emps)
                {
                    var u = emp.User ?? await repo.GetUserByIdAsync(emp.UserId, ct);
                    if (u is null)
                        continue;

                    var matchesUser = (!string.IsNullOrWhiteSpace(targetUsername) &&
                                       string.Equals(u.Username, targetUsername, StringComparison.OrdinalIgnoreCase)) ||
                                      (!string.IsNullOrWhiteSpace(targetEmail) &&
                                       u.HasEmailAddress &&
                                       string.Equals(u.Email, targetEmail, StringComparison.OrdinalIgnoreCase));
                    if (matchesUser)
                    {
                        return new MatchedTenantTarget(
                            repo,
                            false,
                            slug,
                            creds?.OwnerEmail,
                            u.HasEmailAddress ? u.Email : null,
                            u.Username);
                    }
                }
            }
        }

        var hasExplicitTarget = !string.IsNullOrWhiteSpace(targetStoreName) ||
                                !string.IsNullOrWhiteSpace(targetEmail) ||
                                !string.IsNullOrWhiteSpace(targetUsername);
        if (!hasExplicitTarget)
        {
            return allowGenericRootFallback
                ? new MatchedTenantTarget(rootRepository, true, null, null, null, null)
                : null;
        }

        var rootSettings = await rootRepository.GetStoreSettingsAsync(ct);
        if (!string.IsNullOrWhiteSpace(targetStoreName) &&
            string.Equals(rootSettings.StoreName.Trim(), targetStoreName, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchedTenantTarget(rootRepository, true, rootSettings.StoreSlug, null, null, null);
        }

        var rootEmployees = await rootRepository.GetEmployeesAsync(ct);
        foreach (var emp in rootEmployees)
        {
            var u = emp.User ?? await rootRepository.GetUserByIdAsync(emp.UserId, ct);
            if (u is null)
                continue;

            var matchesEmail = !string.IsNullOrWhiteSpace(targetEmail) &&
                               u.HasEmailAddress &&
                               string.Equals(u.Email, targetEmail, StringComparison.OrdinalIgnoreCase);
            var matchesUsername = !string.IsNullOrWhiteSpace(targetUsername) &&
                                  string.Equals(u.Username, targetUsername, StringComparison.OrdinalIgnoreCase);
            if (matchesEmail || matchesUsername)
            {
                return new MatchedTenantTarget(
                    rootRepository,
                    true,
                    rootSettings.StoreSlug,
                    u.HasEmailAddress ? u.Email : null,
                    u.HasEmailAddress ? u.Email : null,
                    u.Username);
            }
        }

        return null;
    }

    public static async Task<AdminStoreSettingsDto?> ApplyRemoteVipAsync(
        RemoteApplyVipRequest request,
        IClothingStoreRepository rootRepository,
        MultiTenantGatewayService gateway,
        CloudAccountRegistryService? accountRegistry,
        CancellationToken ct,
        DateTimeOffset? explicitExpiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var normalizedTier = StoreSettings.NormalizePlanTier(request.PlanTier);
        var isBasicRemoval = string.Equals(normalizedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);
        var effectiveIsCourtesy = !isBasicRemoval && (request.IsCourtesyVip || !request.DurationDays.HasValue);

        CloudAccountSummary? existingCloud = null;
        if (accountRegistry is not null)
        {
            existingCloud = (await accountRegistry.GetPlanStatusWithAccountAsync(
                request.TargetEmail,
                request.TargetUsername,
                null,
                request.StoreName,
                ct))?.Account;
        }

        var hasExplicitNonRootTarget = !string.IsNullOrWhiteSpace(request.StoreName) ||
                                       !string.IsNullOrWhiteSpace(request.TargetEmail) ||
                                       !string.IsNullOrWhiteSpace(request.TargetUsername);

        var targetMatch = await FindMatchingTenantRepositoryAsync(
            rootRepository,
            gateway,
            request.TargetEmail,
            request.TargetUsername,
            request.StoreName,
            existingCloud?.StoreSlug,
            existingCloud,
            allowGenericRootFallback: !hasExplicitNonRootTarget,
            ct);

        StoreSettings? targetSettings = null;
        if (targetMatch is not null)
        {
            targetSettings = await targetMatch.Repository.GetStoreSettingsAsync(ct);
        }

        var canonicalEmail = !string.IsNullOrWhiteSpace(request.TargetEmail)
            ? request.TargetEmail.Trim()
            : targetMatch?.OwnerEmail ?? targetMatch?.EmployeeEmail ?? existingCloud?.Email;
        var canonicalUsername = !string.IsNullOrWhiteSpace(request.TargetUsername) && request.TargetUsername.Trim() != "-"
            ? request.TargetUsername.Trim().TrimStart('@')
            : targetMatch?.EmployeeUsername ?? existingCloud?.Username;
        var canonicalStoreName = !CloudAccountRegistryService.IsGenericStoreIdentifier(request.StoreName)
            ? request.StoreName!.Trim()
            : (!CloudAccountRegistryService.IsGenericStoreIdentifier(targetSettings?.StoreName)
                ? targetSettings!.StoreName
                : existingCloud?.StoreName);
        var canonicalSlug = !CloudAccountRegistryService.IsGenericStoreIdentifier(targetMatch?.Slug)
            ? targetMatch!.Slug
            : (!CloudAccountRegistryService.IsGenericStoreIdentifier(targetSettings?.StoreSlug)
                ? targetSettings!.StoreSlug
                : existingCloud?.StoreSlug);

        CloudAccountSummary? cloudApplied = null;
        if (accountRegistry is not null)
        {
            cloudApplied = await accountRegistry.ApplyOrCreatePlanEntitlementAsync(
                targetEmail: canonicalEmail,
                targetUsername: canonicalUsername,
                storeName: canonicalStoreName,
                storeSlug: canonicalSlug,
                planTier: normalizedTier,
                durationDays: request.DurationDays,
                extendExistingDuration: request.ExtendExistingDuration,
                activateSite: request.ActivateSite,
                ct: ct,
                isCourtesyVip: effectiveIsCourtesy,
                isRecurringSubscription: false,
                explicitExpiresAt: explicitExpiresAt);
        }

        if (targetMatch is not null && targetSettings is not null)
        {
            DateTimeOffset? nextExpiry = explicitExpiresAt;
            if (!isBasicRemoval && !nextExpiry.HasValue && request.DurationDays.HasValue && request.DurationDays.Value > 0)
            {
                var baseStart = request.ExtendExistingDuration &&
                                string.Equals(targetSettings.PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase) &&
                                targetSettings.PlanExpiresAt.HasValue &&
                                targetSettings.PlanExpiresAt.Value > now
                    ? targetSettings.PlanExpiresAt.Value
                    : now;
                nextExpiry = baseStart.AddDays(request.DurationDays.Value);
            }
            else if (isBasicRemoval)
            {
                nextExpiry = null;
            }

            targetSettings.PlanExpiresAt = nextExpiry;
            targetSettings.UpdatePlanAndDesign(normalizedTier, designConfig: targetSettings.DesignConfig);
            if (isBasicRemoval)
            {
                targetSettings.PlanActivatedAt = null;
                targetSettings.ActiveGiftCardCode = null;
                targetSettings.ActivePlanBillingKind = PlanActivationCode.BillingKindPaid;
                targetSettings.IsRecurringSubscription = false;
                targetSettings.ClearBillingGraceAndActivate(null, now);
            }
            else
            {
                targetSettings.PlanActivatedAt = now;
                targetSettings.ActivePlanBillingKind = effectiveIsCourtesy
                    ? PlanActivationCode.BillingKindGiftedUnpaid
                    : PlanActivationCode.BillingKindPaid;
                targetSettings.IsRecurringSubscription = false;
                if (request.ActivateSite)
                {
                    targetSettings.ClearBillingGraceAndActivate(null, now);
                }
                else
                {
                    targetSettings.BillingGraceEndsAt = null;
                    targetSettings.IsBillingSuspended = false;
                    targetSettings.LastPaymentFailureMessage = null;
                }
            }

            await targetMatch.Repository.UpdateStoreSettingsAsync(targetSettings, ct);
            return EmployeeAndSettingsService.MapToAdminSettingsDto(targetSettings);
        }

        if (cloudApplied is not null)
        {
            var virtualSettings = new StoreSettings
            {
                StoreName = string.IsNullOrWhiteSpace(cloudApplied.StoreName) ? request.StoreName ?? "Loja YouLink" : cloudApplied.StoreName,
                StoreSlug = string.IsNullOrWhiteSpace(cloudApplied.StoreSlug) ? string.Empty : cloudApplied.StoreSlug
            };
            virtualSettings.PlanExpiresAt = cloudApplied.PlanExpiresAt;
            virtualSettings.PlanActivatedAt = isBasicRemoval ? null : now;
            virtualSettings.UpdatePlanAndDesign(
                StoreSettings.NormalizePlanTier(cloudApplied.PlanTier),
                designConfig: virtualSettings.DesignConfig);
            virtualSettings.ActivePlanBillingKind = cloudApplied.IsCourtesyVip
                ? PlanActivationCode.BillingKindGiftedUnpaid
                : PlanActivationCode.BillingKindPaid;
            if (cloudApplied.IsSiteActive)
                virtualSettings.ActivateSite();

            return EmployeeAndSettingsService.MapToAdminSettingsDto(virtualSettings);
        }

        return null;
    }

    public static async Task<CloudPlanStatusDto> ResolveCloudPlanStatusAsync(
        string? email,
        string? username,
        string? storeSlug,
        string? storeName,
        string? clientPlanTier,
        DateTimeOffset? clientPlanExpiresAt,
        string? clientAppVersion,
        IClothingStoreRepository rootRepository,
        MultiTenantGatewayService gateway,
        CloudAccountRegistryService? accountRegistry,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (accountRegistry is not null &&
            (!string.IsNullOrWhiteSpace(clientPlanTier) || !string.IsNullOrWhiteSpace(clientAppVersion)))
        {
            await accountRegistry.RecordClientCheckInAsync(
                email,
                username,
                storeSlug,
                storeName,
                clientPlanTier,
                clientPlanExpiresAt,
                clientAppVersion,
                ct);
        }

        var cloudMeta = accountRegistry is not null
            ? await accountRegistry.GetPlanStatusWithAccountAsync(email, username, storeSlug, storeName, ct)
            : null;
        var fromCloud = cloudMeta?.Status;
        var cloudAcc = cloudMeta?.Account;

        var matchedTenant = await FindMatchingTenantRepositoryAsync(
            rootRepository,
            gateway,
            email,
            username,
            storeName,
            storeSlug,
            cloudAcc,
            allowGenericRootFallback: false,
            ct);

        if (matchedTenant is not null)
        {
            var tenantSettings = await matchedTenant.Repository.GetStoreSettingsAsync(ct);
            if (tenantSettings.EvaluateRecurringBillingStatus(now))
                await matchedTenant.Repository.UpdateStoreSettingsAsync(tenantSettings, ct);

            var tenantTier = tenantSettings.GetEffectivePlanTier(now);
            var cloudTier = fromCloud is not null
                ? CloudAccountRegistryService.ResolveEffectivePlan(fromCloud.PlanTier, fromCloud.PlanExpiresAt, now)
                : StoreSettings.PlanTierBasic;

            if (fromCloud?.PlanRevokedAt is { } revokedAt &&
                (!tenantSettings.PlanActivatedAt.HasValue || revokedAt >= tenantSettings.PlanActivatedAt.Value) &&
                string.Equals(cloudTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
            {
                return fromCloud;
            }

            var cloudUpdatedAt = cloudAcc?.PlanUpdatedAt ?? cloudAcc?.UpdatedAt;
            var tenantUpdatedAt = tenantSettings.PlanActivatedAt ?? tenantSettings.UpdatedAt;
            var cloudIsNewerOrHigher = !string.Equals(cloudTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(tenantTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) ||
                 (cloudUpdatedAt.HasValue && cloudUpdatedAt.Value > tenantUpdatedAt) ||
                 PlanCheckoutGuardHelper.GetTierRank(cloudTier) > PlanCheckoutGuardHelper.GetTierRank(tenantTier));

            if (cloudIsNewerOrHigher && fromCloud is not null)
            {
                if (!matchedTenant.IsDefaultRepository &&
                    !string.Equals(tenantTier, cloudTier, StringComparison.OrdinalIgnoreCase))
                {
                    tenantSettings.PlanExpiresAt = fromCloud.PlanExpiresAt;
                    tenantSettings.PlanActivatedAt ??= now;
                    tenantSettings.UpdatePlanAndDesign(cloudTier, designConfig: tenantSettings.DesignConfig);
                    tenantSettings.ActivePlanBillingKind = fromCloud.IsCourtesyVip
                        ? PlanActivationCode.BillingKindGiftedUnpaid
                        : PlanActivationCode.BillingKindPaid;
                    tenantSettings.IsRecurringSubscription = fromCloud.IsRecurringSubscription;
                    if (fromCloud.IsSiteActive)
                        tenantSettings.ClearBillingGraceAndActivate(null, now);
                    await matchedTenant.Repository.UpdateStoreSettingsAsync(tenantSettings, ct);
                }

                return fromCloud;
            }

            if (!string.Equals(tenantTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
            {
                return new CloudPlanStatusDto(
                    PlanTier: tenantTier,
                    PlanExpiresAt: tenantSettings.PlanExpiresAt,
                    IsSiteActive: tenantSettings.IsSiteActive,
                    StoreName: tenantSettings.StoreName,
                    StoreSlug: tenantSettings.StoreSlug,
                    IsCourtesyVip: ResolveTenantIsCourtesyVip(tenantSettings, fromCloud?.IsCourtesyVip),
                    PlanRevokedAt: null,
                    IsRecurringSubscription: tenantSettings.IsRecurringSubscription,
                    BillingGraceEndsAt: tenantSettings.BillingGraceEndsAt,
                    IsBillingSuspended: tenantSettings.IsBillingSuspended,
                    BillingWarningMessage: tenantSettings.LastPaymentFailureMessage);
            }

            if (!string.Equals(cloudTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) ||
                fromCloud?.IsBillingSuspended == true ||
                fromCloud?.BillingGraceEndsAt is not null)
            {
                return fromCloud!;
            }

            return new CloudPlanStatusDto(
                PlanTier: tenantTier,
                PlanExpiresAt: tenantSettings.PlanExpiresAt,
                IsSiteActive: tenantSettings.IsSiteActive,
                StoreName: tenantSettings.StoreName,
                StoreSlug: tenantSettings.StoreSlug,
                IsCourtesyVip: ResolveTenantIsCourtesyVip(tenantSettings, fromCloud?.IsCourtesyVip),
                PlanRevokedAt: null,
                IsRecurringSubscription: tenantSettings.IsRecurringSubscription,
                BillingGraceEndsAt: tenantSettings.BillingGraceEndsAt,
                IsBillingSuspended: tenantSettings.IsBillingSuspended,
                BillingWarningMessage: tenantSettings.LastPaymentFailureMessage);
        }

        if (fromCloud is not null)
            return fromCloud;

        return new CloudPlanStatusDto(
            PlanTier: StoreSettings.PlanTierBasic,
            PlanExpiresAt: null,
            IsSiteActive: true,
            StoreName: null,
            StoreSlug: null);
    }

    public static async Task<RemoteVerifyClientVipResult> VerifyAndRepairClientVipAsync(
        RemoteVerifyClientVipRequest request,
        IClothingStoreRepository rootRepository,
        MultiTenantGatewayService gateway,
        CloudAccountRegistryService? accountRegistry,
        string? publicOrigin,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var expectedTier = StoreSettings.NormalizePlanTier(request.ExpectedAdminPlanTier);
        var adminExpectsVip = !string.IsNullOrWhiteSpace(request.ExpectedAdminPlanTier) &&
                              !string.Equals(expectedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);

        var resolved = await ResolveCloudPlanStatusAsync(
            request.TargetEmail,
            request.TargetUsername,
            request.StoreSlug,
            request.StoreName,
            clientPlanTier: null,
            clientPlanExpiresAt: null,
            clientAppVersion: null,
            rootRepository,
            gateway,
            accountRegistry,
            ct);

        var cloudMeta = accountRegistry is not null
            ? await accountRegistry.GetPlanStatusWithAccountAsync(
                request.TargetEmail,
                request.TargetUsername,
                request.StoreSlug,
                request.StoreName,
                ct)
            : null;

        var matchedTenant = await FindMatchingTenantRepositoryAsync(
            rootRepository,
            gateway,
            request.TargetEmail,
            request.TargetUsername,
            request.StoreName,
            request.StoreSlug,
            cloudMeta?.Account,
            allowGenericRootFallback: false,
            ct);

        var tenantSettings = matchedTenant is not null
            ? await matchedTenant.Repository.GetStoreSettingsAsync(ct)
            : null;

        var serverEffectiveTier = CloudAccountRegistryService.ResolveEffectivePlan(resolved.PlanTier, resolved.PlanExpiresAt, now);
        var cloudTier = cloudMeta?.Status is not null
            ? CloudAccountRegistryService.ResolveEffectivePlan(cloudMeta.Value.Status.PlanTier, cloudMeta.Value.Status.PlanExpiresAt, now)
            : null;
        var tenantTier = tenantSettings?.GetEffectivePlanTier(now);

        var hasServerVip = !string.Equals(serverEffectiveTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);
        var autoRepaired = false;

        if (request.AutoRepairIfDivergent)
        {
            var needsRepairToAdminVip = adminExpectsVip &&
                (!string.Equals(serverEffectiveTier, expectedTier, StringComparison.OrdinalIgnoreCase) ||
                 cloudMeta is null ||
                 (matchedTenant is not null && !string.Equals(tenantTier, expectedTier, StringComparison.OrdinalIgnoreCase)));

            var needsCrossLayerSyncFromServer = !adminExpectsVip &&
                hasServerVip &&
                (cloudMeta is null || (matchedTenant is not null && !string.Equals(tenantTier, serverEffectiveTier, StringComparison.OrdinalIgnoreCase)));

            var isPaidServerProtection = hasServerVip && !resolved.IsCourtesyVip;
            var needsCourtesyRevocation = !string.IsNullOrWhiteSpace(request.ExpectedAdminPlanTier) &&
                !adminExpectsVip &&
                hasServerVip &&
                !isPaidServerProtection;

            if (needsRepairToAdminVip || needsCourtesyRevocation)
            {
                int? repairDays = null;
                if (request.ExpectedAdminExpiresAt.HasValue && request.ExpectedAdminExpiresAt.Value > now)
                {
                    repairDays = Math.Max(1, (int)Math.Ceiling((request.ExpectedAdminExpiresAt.Value - now).TotalDays));
                }

                await ApplyRemoteVipAsync(
                    new RemoteApplyVipRequest(
                        PlanTier: needsCourtesyRevocation ? StoreSettings.PlanTierBasic : expectedTier,
                        DurationDays: repairDays,
                        ExtendExistingDuration: false,
                        ActivateSite: true,
                        StoreName: request.StoreName,
                        TargetUsername: request.TargetUsername,
                        TargetEmail: request.TargetEmail,
                        IsCourtesyVip: !needsCourtesyRevocation && request.IsCourtesyVip),
                    rootRepository,
                    gateway,
                    accountRegistry,
                    ct,
                    explicitExpiresAt: needsCourtesyRevocation ? null : request.ExpectedAdminExpiresAt);
                autoRepaired = true;
            }
            else if (needsCrossLayerSyncFromServer)
            {
                await ApplyRemoteVipAsync(
                    new RemoteApplyVipRequest(
                        PlanTier: serverEffectiveTier,
                        DurationDays: null,
                        ExtendExistingDuration: false,
                        ActivateSite: true,
                        StoreName: resolved.StoreName ?? request.StoreName,
                        TargetUsername: request.TargetUsername,
                        TargetEmail: request.TargetEmail,
                        IsCourtesyVip: resolved.IsCourtesyVip),
                    rootRepository,
                    gateway,
                    accountRegistry,
                    ct,
                    explicitExpiresAt: resolved.PlanExpiresAt);
                autoRepaired = true;
            }

            if (autoRepaired)
            {
                resolved = await ResolveCloudPlanStatusAsync(
                    request.TargetEmail,
                    request.TargetUsername,
                    request.StoreSlug,
                    request.StoreName,
                    null,
                    null,
                    null,
                    rootRepository,
                    gateway,
                    accountRegistry,
                    ct);
                cloudMeta = accountRegistry is not null
                    ? await accountRegistry.GetPlanStatusWithAccountAsync(
                        request.TargetEmail,
                        request.TargetUsername,
                        request.StoreSlug,
                        request.StoreName,
                        ct)
                    : null;
                tenantSettings = matchedTenant is not null
                    ? await matchedTenant.Repository.GetStoreSettingsAsync(ct)
                    : null;

                serverEffectiveTier = CloudAccountRegistryService.ResolveEffectivePlan(resolved.PlanTier, resolved.PlanExpiresAt, now);
                cloudTier = cloudMeta?.Status is not null
                    ? CloudAccountRegistryService.ResolveEffectivePlan(cloudMeta.Value.Status.PlanTier, cloudMeta.Value.Status.PlanExpiresAt, now)
                    : null;
                tenantTier = tenantSettings?.GetEffectivePlanTier(now);
                hasServerVip = !string.Equals(serverEffectiveTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);
            }
        }

        var foundOnServer = cloudMeta is not null || matchedTenant is not null;
        var cloudAcc = cloudMeta?.Account;
        var lastCheckIn = cloudAcc?.LastClientCheckInAt;
        var lastClientTier = cloudAcc?.LastReportedClientPlanTier;
        var lastClientVersion = cloudAcc?.LastClientAppVersion;
        var publishedUrl = matchedTenant is not null && !matchedTenant.IsDefaultRepository && !string.IsNullOrWhiteSpace(publicOrigin) && !string.IsNullOrWhiteSpace(matchedTenant.Slug)
            ? WhatsAppLinkBuilder.BuildCentralTenantCatalogUrl(publicOrigin, matchedTenant.Slug)
            : null;

        string statusCode;
        string summary;

        if (!foundOnServer)
        {
            statusCode = "NotFoundOnServer";
            summary = "Conta ainda não encontrada no Servidor Central (o usuário precisa entrar no app com internet ou o Admin pode conceder VIP direto para pré-liberar).";
        }
        else if (autoRepaired)
        {
            statusCode = "AutoRepaired";
            summary = $"🔧 Divergência corrigida! Servidor central agora está em '{serverEffectiveTier}'. " +
                      (lastCheckIn.HasValue
                          ? $"Último acesso do app do cliente foi em {lastCheckIn.Value.ToLocalTime():dd/MM HH:mm} com '{lastClientTier ?? "-"}'; na próxima abertura/atualização receberá '{serverEffectiveTier}'."
                          : "O aplicativo do cliente receberá automaticamente assim que abrir ou sincronizar.");
        }
        else if (adminExpectsVip && !string.Equals(serverEffectiveTier, expectedTier, StringComparison.OrdinalIgnoreCase))
        {
            statusCode = "Divergent";
            summary = $"⚠️ Divergência detectada: Admin espera '{expectedTier}', mas servidor central ainda está em '{serverEffectiveTier}'.";
        }
        else if (lastCheckIn.HasValue &&
                 string.Equals(lastClientTier, serverEffectiveTier, StringComparison.OrdinalIgnoreCase))
        {
            statusCode = "VerifiedSynced";
            summary = $"✅ Client verificado e sincronizado! App do usuário confirmou plano '{lastClientTier}' em {lastCheckIn.Value.ToLocalTime():dd/MM HH:mm}" +
                      (string.IsNullOrWhiteSpace(lastClientVersion) ? "." : $" (v{lastClientVersion}).");
        }
        else if (lastCheckIn.HasValue && !string.IsNullOrWhiteSpace(lastClientTier))
        {
            statusCode = "ServerReadyAwaitingClient";
            summary = $"⏳ Servidor OK ('{serverEffectiveTier}'), mas o último check-in do client em {lastCheckIn.Value.ToLocalTime():dd/MM HH:mm} reportou '{lastClientTier}'. Peça ao usuário para abrir/atualizar o app para aplicar agora.";
        }
        else
        {
            statusCode = "ServerReadyAwaitingClient";
            summary = $"⏳ Servidor OK ('{serverEffectiveTier}'), aguardando o aplicativo do usuário abrir/sincronizar para confirmar o recebimento.";
        }

        return new RemoteVerifyClientVipResult(
            FoundOnServer: foundOnServer,
            HasActiveServerVip: hasServerVip,
            ServerEffectivePlanTier: serverEffectiveTier,
            ServerPlanExpiresAt: resolved.PlanExpiresAt,
            HasCloudAccountRecord: cloudMeta is not null,
            CloudRegistryPlanTier: cloudTier,
            HasPublishedTenantRepo: matchedTenant is not null && !matchedTenant.IsDefaultRepository,
            PublishedTenantPlanTier: tenantTier,
            PublishedStoreUrl: publishedUrl,
            LastClientCheckInAt: lastCheckIn,
            LastReportedClientPlanTier: lastClientTier,
            LastClientAppVersion: lastClientVersion,
            AutoRepaired: autoRepaired,
            StatusCode: statusCode,
            SummaryMessage: summary);
    }
}
