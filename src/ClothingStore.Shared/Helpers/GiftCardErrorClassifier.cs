namespace ClothingStore.Shared.Helpers;

/// <summary>
/// Classificação padronizada dos estados e erros de validação/resgate de Gift Card entre servidor, Admin e app loja.
/// </summary>
public static class GiftCardErrorClassifier
{
    public const string StatusAvailable = "Available";
    public const string StatusNotFound = "NotFound";
    public const string StatusAlreadyRedeemed = "AlreadyRedeemed";
    public const string StatusRevoked = "Revoked";
    public const string StatusExpired = "Expired";
    public const string StatusStoreMismatch = "StoreMismatch";
    public const string StatusInvalidInput = "InvalidInput";

    public static string ClassifyMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return StatusInvalidInput;

        var msg = message.Trim();
        if (msg.Contains("não encontrado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("nao encontrado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return StatusNotFound;
        }

        if (msg.Contains("já foi resgatado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("ja foi resgatado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("already redeemed", StringComparison.OrdinalIgnoreCase))
        {
            return StatusAlreadyRedeemed;
        }

        if (msg.Contains("pausado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("revogado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("revoked", StringComparison.OrdinalIgnoreCase))
        {
            return StatusRevoked;
        }

        if (msg.Contains("expirou", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("expirado", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            return StatusExpired;
        }

        if (msg.Contains("exclusivamente para a loja", StringComparison.OrdinalIgnoreCase))
        {
            return StatusStoreMismatch;
        }

        return StatusInvalidInput;
    }
}
