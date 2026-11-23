namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Flags centralizadas de recursos do YouLink.
/// Atualmente configurado em modo 100% Catálogo Digital (sem funções de telefone/WhatsApp e sem fluxo de vendas/pedidos).
/// </summary>
public static class CatalogFeatureFlags
{
    public static bool PhoneFeaturesEnabled { get; } = false;
    public static bool SalesAndOrdersEnabled { get; } = false;
}
