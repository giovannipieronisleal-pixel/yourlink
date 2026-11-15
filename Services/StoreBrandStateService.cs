using ClothingStore.Application.Services;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor.Services;

/// <summary>
/// Mantém o estado visual ativo do aplicativo e da loja (Minimalista Luxo no Plano Básico,
/// ou design moldado pelo cliente no Plano Premium) com notificação imediata para a UI.
/// </summary>
public sealed class StoreBrandStateService
{
    private readonly CatalogService _catalogService;
    private bool _initialized;

    public string CurrentPlanTier { get; private set; } = StoreDesignPresets.PlanBasic;
    public DateTimeOffset? PlanActivatedAt { get; private set; }
    public DateTimeOffset? PlanExpiresAt { get; private set; }
    public string? ActiveGiftCardCode { get; private set; }

    public bool IsPlanExpired => PlanCheckoutGuardHelper.IsPlanExpired(PlanExpiresAt);

    public bool IsLicenseValid => !IsPlanExpired;

    public string? ActivePlanTier => PlanCheckoutGuardHelper.ResolveActivePlanTier(
        CurrentPlanTier,
        PlanActivatedAt,
        PlanExpiresAt,
        ActiveGiftCardCode);

    public bool HasActivePlan => !string.IsNullOrWhiteSpace(ActivePlanTier);

    public bool IsBasicPlan =>
        string.Equals(ActivePlanTier, StoreDesignPresets.PlanBasic, StringComparison.OrdinalIgnoreCase);

    public bool IsPremiumPlan =>
        string.Equals(ActivePlanTier, StoreDesignPresets.PlanPremium, StringComparison.OrdinalIgnoreCase);

    public bool IsIntermediatePlan =>
        string.Equals(ActivePlanTier, StoreDesignPresets.PlanIntermediate, StringComparison.OrdinalIgnoreCase);

    public string EffectivePlanTier =>
        IsPremiumPlan ? StoreDesignPresets.PlanPremium
        : IsIntermediatePlan ? StoreDesignPresets.PlanIntermediate
        : StoreDesignPresets.PlanBasic;

    public StoreDesignDto EffectiveDesign { get; private set; } = StoreDesignDto.CreateMinimalistaLuxo();

    public event Action? OnBrandChanged;

    public StoreBrandStateService(CatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_initialized)
            return;

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        try
        {
            var settings = await _catalogService.GetPublicStoreSettingsAsync();
            ApplyFromPublicSettings(settings);
            _initialized = true;
        }
        catch
        {
            EffectiveDesign = StoreDesignDto.CreateMinimalistaLuxo();
            _initialized = true;
        }
    }

    public void ApplyFromPublicSettings(PublicStoreSettingsDto settings)
    {
        PlanActivatedAt = settings.PlanActivatedAt;
        PlanExpiresAt = settings.PlanExpiresAt;
        ActiveGiftCardCode = settings.ActiveGiftCardCode;
        CurrentPlanTier = StoreDesignPresets.NormalizePlanTier(settings.PlanTier);
        EffectiveDesign = ResolveDesignForCurrentPlan(settings.Design);

        OnBrandChanged?.Invoke();
    }

    public void ApplyFromAdminSettings(AdminStoreSettingsDto settings)
    {
        PlanActivatedAt = settings.PlanActivatedAt;
        PlanExpiresAt = settings.PlanExpiresAt;
        ActiveGiftCardCode = settings.ActiveGiftCardCode;
        CurrentPlanTier = StoreDesignPresets.NormalizePlanTier(settings.PlanTier);
        EffectiveDesign = ResolveDesignForCurrentPlan(settings.Design);

        _initialized = true;
        OnBrandChanged?.Invoke();
    }

    public void PreviewDraft(StoreDesignDto draft)
    {
        EffectiveDesign = IsIntermediatePlan
            ? StoreDesignPresets.ResolveByPresetKey(draft?.PresetKey)
            : StoreDesignCssHelper.Sanitize(draft);
        OnBrandChanged?.Invoke();
    }

    private StoreDesignDto ResolveDesignForCurrentPlan(StoreDesignDto? design) => EffectivePlanTier switch
    {
        StoreDesignPresets.PlanPremium => StoreDesignCssHelper.Sanitize(design),
        StoreDesignPresets.PlanIntermediate => StoreDesignPresets.ResolveByPresetKey(design?.PresetKey),
        _ => StoreDesignDto.CreateMinimalistaLuxo()
    };

    public string BuildInlineCssVariables() => StoreDesignCssHelper.BuildInlineStyle(EffectiveDesign);

    public string BuildGoogleFontsUrl() => StoreDesignCssHelper.BuildGoogleFontsUrl(EffectiveDesign);
}
