namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Flags centralizadas de recursos do YouLink.
/// Vendas via Pix / Mercado Pago ativas com atendimento via Chat Interno do App (sem WhatsApp/telefone).
/// </summary>
public static class CatalogFeatureFlags
{
    public static bool PhoneFeaturesEnabled { get; } = false;
    public static bool SalesAndOrdersEnabled { get; } = true;
}
