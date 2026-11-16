using System.Text.Json;
using ClothingStore.Shared.Helpers;
using Microsoft.JSInterop;

namespace ClothingStore.Web.Services;

/// <summary>
/// Gerencia a sacola multi-itens do visitante persistida no localStorage do navegador (custo zero no servidor)
/// e isolada por loja (storeSlug).
/// </summary>
public sealed class StoreCartService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly List<CartItemEntry> _items = [];
    private string? _loadedStoreKey;

    public event Action? OnChanged;

    public bool IsDrawerOpen { get; private set; }
    public IReadOnlyList<CartItemEntry> Items => _items;
    public int TotalQuantity => _items.Sum(i => Math.Max(1, i.Quantity));
    public decimal SubtotalAmount => _items.Sum(i => i.UnitPrice * Math.Max(1, i.Quantity));

    public void OpenDrawer()
    {
        IsDrawerOpen = true;
        OnChanged?.Invoke();
    }

    public void CloseDrawer()
    {
        if (!IsDrawerOpen)
            return;

        IsDrawerOpen = false;
        OnChanged?.Invoke();
    }

    public async Task EnsureLoadedAsync(IJSRuntime js, string? storeSlug)
    {
        var key = BuildStorageKey(storeSlug);
        if (string.Equals(_loadedStoreKey, key, StringComparison.Ordinal))
            return;

        _loadedStoreKey = key;
        _items.Clear();

        try
        {
            var rawJson = await js.InvokeAsync<string>("youlinkStore.getCart", key);
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                var deserialized = JsonSerializer.Deserialize<List<CartItemEntry>>(rawJson, JsonOptions);
                if (deserialized is { Count: > 0 })
                {
                    foreach (var item in deserialized.Where(i => i.ProductId != Guid.Empty && !string.IsNullOrWhiteSpace(i.ProductName)))
                    {
                        _items.Add(item with { Quantity = Math.Max(1, item.Quantity) });
                    }
                }
            }
        }
        catch
        {
            // Durante prerendering SSR ou navegação sem JS ativo, mantém estado vazio sem falhar.
        }

        OnChanged?.Invoke();
    }

    public async Task AddItemAsync(IJSRuntime js, string? storeSlug, CartItemEntry newItem)
    {
        await EnsureLoadedAsync(js, storeSlug);

        var maxCap = newItem.MaxStock > 0 ? newItem.MaxStock : 99;
        var existingIdx = _items.FindIndex(i =>
            i.ProductId == newItem.ProductId &&
            string.Equals(i.Color ?? string.Empty, newItem.Color ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Size ?? string.Empty, newItem.Size ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (existingIdx >= 0)
        {
            var existing = _items[existingIdx];
            var nextQty = Math.Min(maxCap, existing.Quantity + Math.Max(1, newItem.Quantity));
            _items[existingIdx] = existing with
            {
                Quantity = nextQty,
                MaxStock = maxCap,
                UnitPrice = newItem.UnitPrice,
                ImageUrl = newItem.ImageUrl ?? existing.ImageUrl
            };
        }
        else
        {
            _items.Add(newItem with
            {
                Quantity = Math.Clamp(newItem.Quantity, 1, maxCap),
                MaxStock = maxCap
            });
        }

        await PersistAsync(js, storeSlug);
        OnChanged?.Invoke();
    }

    public async Task UpdateQuantityAsync(IJSRuntime js, string? storeSlug, int index, int delta)
    {
        if (index < 0 || index >= _items.Count)
            return;

        var item = _items[index];
        var nextQty = item.Quantity + delta;
        if (nextQty <= 0)
        {
            _items.RemoveAt(index);
        }
        else
        {
            var maxCap = item.MaxStock > 0 ? item.MaxStock : 99;
            _items[index] = item with { Quantity = Math.Min(maxCap, nextQty) };
        }

        await PersistAsync(js, storeSlug);
        OnChanged?.Invoke();
    }

    public async Task RemoveItemAsync(IJSRuntime js, string? storeSlug, int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        _items.RemoveAt(index);
        await PersistAsync(js, storeSlug);
        OnChanged?.Invoke();
    }

    public async Task ClearAsync(IJSRuntime js, string? storeSlug)
    {
        _items.Clear();
        await PersistAsync(js, storeSlug);
        OnChanged?.Invoke();
    }

    public IReadOnlyList<WhatsAppLinkBuilder.CartWhatsAppItem> ToWhatsAppItems() =>
        _items.Select(i => new WhatsAppLinkBuilder.CartWhatsAppItem(
            i.ProductName,
            i.Color,
            i.Size,
            i.Quantity,
            i.UnitPrice)).ToList();

    private async Task PersistAsync(IJSRuntime js, string? storeSlug)
    {
        var key = BuildStorageKey(storeSlug);
        _loadedStoreKey = key;
        try
        {
            var json = JsonSerializer.Serialize(_items, JsonOptions);
            await js.InvokeVoidAsync("youlinkStore.saveCart", key, json);
        }
        catch
        {
        }
    }

    public static string BuildStorageKey(string? storeSlug)
    {
        if (string.IsNullOrWhiteSpace(storeSlug))
            return "youlink_cart_v1_default";

        return $"youlink_cart_v1_{WhatsAppLinkBuilder.GenerateStoreSlug(storeSlug)}";
    }

    public sealed record CartItemEntry(
        Guid ProductId,
        string ProductSlug,
        string ProductName,
        string? Color,
        string? Size,
        decimal UnitPrice,
        int Quantity,
        int MaxStock,
        string? ImageUrl);
}
