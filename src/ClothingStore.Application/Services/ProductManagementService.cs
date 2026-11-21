using ClothingStore.Application.Interfaces;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Shared.DTOs;

namespace ClothingStore.Application.Services;

/// <summary>
/// Serviço administrativo de gerenciamento de categorias, produtos, variantes e fotografias.
/// </summary>
public sealed class ProductManagementService
{
    private readonly IClothingStoreRepository _repository;
    private readonly IImageStorageService _imageStorage;

    public ProductManagementService(
        IClothingStoreRepository repository,
        IImageStorageService imageStorage)
    {
        _repository = repository;
        _imageStorage = imageStorage;
    }

    public async Task<IReadOnlyList<CategoryDto>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        var products = await _repository.GetProductsAsync(includeArchived: false, ct);

        return categories
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Description,
                c.DisplayOrder,
                c.IsActive,
                products.Count(p => p.CategoryId == c.Id)))
            .ToList();
    }

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default)
    {
        var category = new Category(request.Name, request.Description, request.DisplayOrder)
        {
            IsActive = request.IsActive
        };

        await _repository.AddCategoryAsync(category, ct);
        return new CategoryDto(category.Id, category.Name, category.Slug, category.Description, category.DisplayOrder, category.IsActive, 0);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken ct = default)
    {
        var category = await _repository.GetCategoryByIdAsync(id, ct)
            ?? throw new DomainException("Categoria não encontrada.");

        category.UpdateDetails(request.Name, request.Description, request.DisplayOrder, request.IsActive);
        await _repository.UpdateCategoryAsync(category, ct);

        var products = await _repository.GetProductsAsync(includeArchived: false, ct);
        var count = products.Count(p => p.CategoryId == category.Id);
        return new CategoryDto(category.Id, category.Name, category.Slug, category.Description, category.DisplayOrder, category.IsActive, count);
    }

    public async Task<IReadOnlyList<ProductDetailDto>> GetAdminProductsAsync(
        string? search = null,
        Guid? categoryId = null,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var products = await _repository.GetProductsAsync(includeArchived, ct);
        var categories = (await _repository.GetCategoriesAsync(onlyActive: false, ct)).ToDictionary(c => c.Id);

        var query = products.AsEnumerable();

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                p.Variants.Any(v => v.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        return query
            .Select(p =>
            {
                categories.TryGetValue(p.CategoryId, out var cat);
                return CatalogService.MapToProductDetailDto(p, cat);
            })
            .ToList();
    }

    public async Task<ProductDetailDto> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var category = await _repository.GetCategoryByIdAsync(product.CategoryId, ct);
        return CatalogService.MapToProductDetailDto(product, category);
    }

    private async Task<Category> ResolveOrCreateCategoryAsync(
        Guid categoryId,
        string? categoryName,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var trimmedName = categoryName.Trim();
            var slug = Category.GenerateSlug(trimmedName);
            var existingCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
            var matched = existingCategories.FirstOrDefault(c =>
                string.Equals(c.Name, trimmedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
                return matched;

            var created = new Category(trimmedName, null, existingCategories.Count + 1)
            {
                IsActive = true
            };
            await _repository.AddCategoryAsync(created, ct);
            return created;
        }

        if (categoryId != Guid.Empty)
        {
            var byId = await _repository.GetCategoryByIdAsync(categoryId, ct);
            if (byId is not null)
                return byId;
        }

        var allCategories = await _repository.GetCategoriesAsync(onlyActive: false, ct);
        if (allCategories.Count > 0)
            return allCategories[0];

        throw new DomainException("Nenhuma categoria cadastrada ainda. Crie ou digite o nome da sua categoria ao adicionar o produto.");
    }

    public async Task<ProductDetailDto> CreateProductAsync(
        SaveProductRequest request,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        var category = await ResolveOrCreateCategoryAsync(request.CategoryId, request.CategoryName, ct);
        var trimmedName = (request.Name ?? string.Empty).Trim();

        // Monta a lista de variantes solicitadas (grade múltipla ou variante inicial única)
        var requestedVariants = new List<SaveProductVariantRequest>();
        if (request.InitialVariants is { Count: > 0 })
        {
            foreach (var v in request.InitialVariants)
            {
                if (v is null) continue;
                var hasAnyField = !string.IsNullOrWhiteSpace(v.Color) ||
                                  !string.IsNullOrWhiteSpace(v.Size) ||
                                  v.InitialStockQuantity > 0;
                if (!hasAnyField) continue;

                requestedVariants.Add(new SaveProductVariantRequest
                {
                    Color = string.IsNullOrWhiteSpace(v.Color) ? "Padrão" : v.Color.Trim(),
                    ColorHex = v.ColorHex,
                    Size = string.IsNullOrWhiteSpace(v.Size) ? "Único" : v.Size.Trim(),
                    CustomSku = v.CustomSku,
                    InitialStockQuantity = Math.Max(0, v.InitialStockQuantity),
                    LowStockThreshold = v.LowStockThreshold > 0 ? v.LowStockThreshold : 3,
                    IsActive = v.IsActive
                });
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.InitialColor) ||
                 !string.IsNullOrWhiteSpace(request.InitialSize) ||
                 request.InitialStockQuantity > 0)
        {
            var resolvedColor = string.IsNullOrWhiteSpace(request.InitialColor) ? "Padrão" : request.InitialColor.Trim();
            var resolvedSize = string.IsNullOrWhiteSpace(request.InitialSize) ? "Único" : request.InitialSize.Trim();
            requestedVariants.Add(new SaveProductVariantRequest
            {
                Color = resolvedColor,
                ColorHex = ProductVariant.ResolveDefaultColorHex(resolvedColor),
                Size = resolvedSize,
                InitialStockQuantity = Math.Max(0, request.InitialStockQuantity),
                LowStockThreshold = 3,
                IsActive = true
            });
        }

        // Evita duplicação acidental: se já existir produto ativo com exatamente o mesmo nome na mesma categoria
        // e o usuário estiver cadastrando variantes/modelos, junta as variantes dentro do mesmo produto!
        var existingProducts = await _repository.GetProductsAsync(includeArchived: false, ct);
        var existingMatch = existingProducts.FirstOrDefault(p =>
            p.CategoryId == category.Id &&
            string.Equals(p.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));

        Product product;
        if (existingMatch is not null && requestedVariants.Count > 0)
        {
            product = existingMatch;
            if (request.BasePrice > 0 && product.BasePrice != request.BasePrice)
            {
                product.UpdateDetails(
                    category.Id,
                    product.Name,
                    string.IsNullOrWhiteSpace(request.Description) ? product.Description : request.Description,
                    request.BasePrice,
                    request.IsVisibleOnSite);
            }
        }
        else
        {
            product = new Product(
                category.Id,
                trimmedName,
                request.Description,
                request.BasePrice,
                request.IsVisibleOnSite);

            await _repository.AddProductAsync(product, ct);
        }

        if (requestedVariants.Count > 0)
        {
            var effectiveEmpId = employeeId.HasValue && employeeId.Value != Guid.Empty
                ? employeeId.Value
                : (await _repository.GetEmployeesAsync(ct)).FirstOrDefault()?.Id ?? Guid.Empty;

            foreach (var rv in requestedVariants)
            {
                var existingVar = product.Variants.FirstOrDefault(v =>
                    string.Equals(v.Color, rv.Color, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(v.Size, rv.Size, StringComparison.OrdinalIgnoreCase));

                ProductVariant targetVariant;
                if (existingVar is not null)
                {
                    targetVariant = existingVar;
                }
                else
                {
                    var colorHex = string.IsNullOrWhiteSpace(rv.ColorHex)
                        ? ProductVariant.ResolveDefaultColorHex(rv.Color)
                        : rv.ColorHex;
                    targetVariant = product.AddVariant(
                        rv.Color,
                        rv.Size,
                        colorHex,
                        rv.CustomSku,
                        rv.LowStockThreshold);
                }

                await _repository.UpdateProductAsync(product, ct);

                if (rv.InitialStockQuantity > 0 && effectiveEmpId != Guid.Empty)
                {
                    var movement = targetVariant.ApplyStockMovement(
                        effectiveEmpId,
                        StockMovementType.In,
                        rv.InitialStockQuantity,
                        existingMatch is not null
                            ? "Entrada ao juntar modelo/variante no produto"
                            : "Estoque inicial no cadastro do produto");

                    await _repository.RecordStockMovementAsync(targetVariant, movement, ct);
                    await _repository.UpdateProductAsync(product, ct);
                }
            }
        }

        return CatalogService.MapToProductDetailDto(product, category);
    }

    public async Task<int> MergeDuplicateProductsByNameAsync(Guid? employeeId = null, CancellationToken ct = default)
    {
        var products = (await _repository.GetProductsAsync(includeArchived: false, ct)).ToList();
        var groups = products
            .GroupBy(p => $"{p.CategoryId:N}|{p.Name.Trim().ToLowerInvariant()}")
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0)
            return 0;

        var effectiveEmpId = employeeId.HasValue && employeeId.Value != Guid.Empty
            ? employeeId.Value
            : (await _repository.GetEmployeesAsync(ct)).FirstOrDefault()?.Id ?? Guid.Empty;

        var mergedCount = 0;
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(p => p.Images.Count)
                .ThenByDescending(p => p.Variants.Count)
                .ThenBy(p => p.CreatedAt)
                .ToList();

            var primary = ordered[0];
            foreach (var dup in ordered.Skip(1))
            {
                foreach (var dupImg in dup.Images)
                {
                    if (!primary.Images.Any(i => string.Equals(i.PublicUrl, dupImg.PublicUrl, StringComparison.OrdinalIgnoreCase)))
                    {
                        await AttachImageToProductAsync(primary, dupImg.StoragePath, dupImg.PublicUrl, dupImg.AltText, primary.Images.Count == 0, ct);
                    }
                }

                foreach (var dupVar in dup.Variants)
                {
                    var matchVar = primary.Variants.FirstOrDefault(v =>
                        string.Equals(v.Color, dupVar.Color, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(v.Size, dupVar.Size, StringComparison.OrdinalIgnoreCase));

                    if (matchVar is null)
                    {
                        var added = primary.AddVariant(dupVar.Color, dupVar.Size, dupVar.ColorHex, customSku: null, lowStockThreshold: dupVar.LowStockThreshold);
                        await _repository.UpdateProductAsync(primary, ct);
                        if (dupVar.StockQuantity > 0 && effectiveEmpId != Guid.Empty)
                        {
                            var mov = added.ApplyStockMovement(effectiveEmpId, StockMovementType.In, dupVar.StockQuantity, "Unificação de produto duplicado");
                            await _repository.RecordStockMovementAsync(added, mov, ct);
                        }
                    }
                    else if (dupVar.StockQuantity > 0 && effectiveEmpId != Guid.Empty)
                    {
                        var mov = matchVar.ApplyStockMovement(effectiveEmpId, StockMovementType.In, dupVar.StockQuantity, "Soma de estoque na unificação de produto duplicado");
                        await _repository.RecordStockMovementAsync(matchVar, mov, ct);
                    }
                }

                await _repository.UpdateProductAsync(primary, ct);
                await _repository.DeleteOrArchiveProductAsync(dup.Id, ct);
                mergedCount++;
            }
        }

        return mergedCount;
    }

    public async Task<ProductDetailDto> UpdateProductAsync(Guid productId, SaveProductRequest request, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var category = await ResolveOrCreateCategoryAsync(request.CategoryId, request.CategoryName, ct);

        product.UpdateDetails(
            category.Id,
            request.Name,
            request.Description,
            request.BasePrice,
            request.IsVisibleOnSite);

        await _repository.UpdateProductAsync(product, ct);
        return CatalogService.MapToProductDetailDto(product, category);
    }

    public async Task<ProductDetailDto> ToggleVisibilityAsync(Guid productId, bool isVisibleOnSite, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        product.IsVisibleOnSite = isVisibleOnSite;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.UpdateProductAsync(product, ct);

        var category = await _repository.GetCategoryByIdAsync(product.CategoryId, ct);
        return CatalogService.MapToProductDetailDto(product, category);
    }

    public async Task DeleteOrArchiveProductAsync(Guid productId, CancellationToken ct = default)
    {
        await _repository.DeleteOrArchiveProductAsync(productId, ct);
    }

    // =========================================================================
    // VARIANTES (Cor + Tamanho + Estoque Independente)
    // =========================================================================
    public async Task<ProductVariantDto> AddVariantAsync(
        Guid productId,
        Guid employeeId,
        SaveProductVariantRequest request,
        CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var variant = product.AddVariant(
            request.Color,
            request.Size,
            request.ColorHex,
            request.CustomSku,
            request.LowStockThreshold);

        await _repository.UpdateProductAsync(product, ct);

        if (request.InitialStockQuantity > 0 && employeeId != Guid.Empty)
        {
            var movement = variant.ApplyStockMovement(
                employeeId,
                StockMovementType.In,
                request.InitialStockQuantity,
                "Estoque inicial no cadastro da variante");

            await _repository.RecordStockMovementAsync(variant, movement, ct);
            await _repository.UpdateProductAsync(product, ct);
        }

        return new ProductVariantDto(
            variant.Id,
            variant.ProductId,
            variant.Sku,
            variant.Color,
            variant.ColorHex ?? ProductVariant.ResolveDefaultColorHex(variant.Color),
            variant.Size,
            variant.StockQuantity,
            variant.LowStockThreshold,
            variant.IsActive,
            variant.IsLowStock,
            variant.IsOutOfStock);
    }

    public async Task<ProductVariantDto> UpdateVariantAsync(
        Guid variantId,
        SaveProductVariantRequest request,
        Guid? employeeId = null,
        CancellationToken ct = default)
    {
        var variant = await _repository.GetVariantByIdAsync(variantId, ct)
            ?? throw new DomainException("Variante não encontrada.");

        var product = await _repository.GetProductByIdAsync(variant.ProductId, ct)
            ?? throw new DomainException("Produto da variante não encontrado.");

        var normalizedColor = string.IsNullOrWhiteSpace(request.Color) ? ProductVariant.DefaultColorName : request.Color.Trim();
        var normalizedSize = string.IsNullOrWhiteSpace(request.Size) ? ProductVariant.DefaultSizeName : request.Size.Trim().ToUpperInvariant();

        var duplicate = product.Variants.Any(v =>
            v.Id != variantId &&
            v.IsActive &&
            string.Equals(v.Color, normalizedColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.Size, normalizedSize, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
            throw new DomainException("Já existe outra variante ativa com esta mesma cor e tamanho neste produto.");

        var sku = string.IsNullOrWhiteSpace(request.CustomSku) ? variant.Sku : request.CustomSku;
        variant.UpdateDetails(sku, normalizedColor, normalizedSize, request.ColorHex, request.LowStockThreshold, request.IsActive);

        if (request.UpdateStockQuantity && request.InitialStockQuantity >= 0 && request.InitialStockQuantity != variant.StockQuantity)
        {
            var effectiveEmpId = employeeId.HasValue && employeeId.Value != Guid.Empty
                ? employeeId.Value
                : (await _repository.GetEmployeesAsync(ct)).FirstOrDefault()?.Id ?? Guid.Empty;

            var movement = variant.ApplyStockMovement(
                effectiveEmpId,
                StockMovementType.Adjustment,
                request.InitialStockQuantity,
                "Ajuste direto na edição da variante");

            await _repository.RecordStockMovementAsync(variant, movement, ct);
        }

        await _repository.UpdateProductAsync(product, ct);

        return new ProductVariantDto(
            variant.Id,
            variant.ProductId,
            variant.Sku,
            variant.Color,
            variant.ColorHex ?? ProductVariant.ResolveDefaultColorHex(variant.Color),
            variant.Size,
            variant.StockQuantity,
            variant.LowStockThreshold,
            variant.IsActive,
            variant.IsLowStock,
            variant.IsOutOfStock);
    }

    public async Task DeleteVariantAsync(Guid variantId, CancellationToken ct = default)
    {
        var variant = await _repository.GetVariantByIdAsync(variantId, ct)
            ?? throw new DomainException("Variante não encontrada.");

        var product = await _repository.GetProductByIdAsync(variant.ProductId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var movements = await _repository.GetStockMovementsAsync(variantId: variantId, limit: 1, ct: ct);
        if (movements.Count > 0)
        {
            variant.IsActive = false;
            variant.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            product.Variants.RemoveAll(v => v.Id == variantId);
        }

        await _repository.UpdateProductAsync(product, ct);
    }

    // =========================================================================
    // IMAGENS DO PRODUTO (Supabase Storage + Galeria)
    // =========================================================================
    public async Task<ProductImageDto> UploadProductImageAsync(
        Guid productId,
        string fileName,
        Stream contentStream,
        string contentType,
        string? altText,
        bool isPrimary,
        CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var (storagePath, publicUrl) = await _imageStorage.UploadImageAsync(
            $"products/{productId:N}",
            fileName,
            contentStream,
            contentType,
            ct);

        return await AttachImageToProductAsync(product, storagePath, publicUrl, altText, isPrimary, ct);
    }

    private static readonly HttpClient ExternalImageHttpClient = CreateExternalImageHttpClient();

    private static HttpClient CreateExternalImageHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        return client;
    }

    public async Task<ProductImageDto> AddProductImageByUrlAsync(
        Guid productId,
        AddProductImageByUrlRequest request,
        CancellationToken ct = default)
    {
        var normalizedInput = ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.NormalizeExternalImageUrl(request?.ImageUrl);
        if (!ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.IsSafeMediaUrl(normalizedInput))
            throw new DomainException("Informe uma URL de imagem válida (https://, http:// ou data:image segura).");

        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var sanitizedUrl = ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.SanitizeMediaUrl(normalizedInput);

        if (Uri.TryCreate(sanitizedUrl, UriKind.Absolute, out var remoteUri) &&
            (string.Equals(remoteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(remoteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) &&
            !remoteUri.IsLoopback)
        {
            var downloaded = await TryDownloadExternalImageAsync(productId, remoteUri, ct);
            if (downloaded.HasValue)
            {
                return await AttachImageToProductAsync(
                    product,
                    downloaded.Value.StoragePath,
                    downloaded.Value.PublicUrl,
                    request!.AltText,
                    request.IsPrimary,
                    ct);
            }

            if (!string.IsNullOrWhiteSpace(_lastResolvedOgImageUrl) &&
                ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.IsSafeMediaUrl(_lastResolvedOgImageUrl))
            {
                sanitizedUrl = ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.SanitizeMediaUrl(_lastResolvedOgImageUrl);
            }
        }

        return await AttachImageToProductAsync(
            product,
            sanitizedUrl,
            sanitizedUrl,
            request!.AltText,
            request.IsPrimary,
            ct);
    }

    private string? _lastResolvedOgImageUrl;

    private async Task<(string StoragePath, string PublicUrl)?> TryDownloadExternalImageAsync(
        Guid productId,
        Uri initialUri,
        CancellationToken ct)
    {
        _lastResolvedOgImageUrl = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            var targetUri = initialUri;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUri);
                using var resp = await ExternalImageHttpClient.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                if (!resp.IsSuccessStatusCode)
                    return null;

                var mediaType = resp.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant() ?? string.Empty;
                if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && attempt == 0)
                {
                    var html = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                    var ogUrl = ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.ExtractOpenGraphImageUrl(html, targetUri);
                    if (!string.IsNullOrWhiteSpace(ogUrl) &&
                        ClothingStore.Shared.Helpers.WhatsAppLinkBuilder.IsSafeMediaUrl(ogUrl) &&
                        Uri.TryCreate(ogUrl, UriKind.Absolute, out var nextUri) &&
                        !nextUri.IsLoopback)
                    {
                        _lastResolvedOgImageUrl = ogUrl;
                        targetUri = nextUri;
                        continue;
                    }
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token);
                if (bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024)
                    return null;

                var detectedMime = DetectImageMimeType(bytes, mediaType);
                if (string.IsNullOrWhiteSpace(detectedMime))
                    return null;

                var ext = detectedMime switch
                {
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    "image/gif" => ".gif",
                    _ => ".jpg"
                };

                await using var ms = new MemoryStream(bytes);
                return await _imageStorage.UploadImageAsync(
                    $"products/{productId:N}",
                    $"url_import{ext}",
                    ms,
                    detectedMime,
                    ct);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? DetectImageMimeType(byte[] bytes, string? declaredMediaType)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";

        if (!string.IsNullOrWhiteSpace(declaredMediaType) &&
            declaredMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !declaredMediaType.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return declaredMediaType;
        }

        return null;
    }

    private async Task<ProductImageDto> AttachImageToProductAsync(
        Product product,
        string storagePath,
        string publicUrl,
        string? altText,
        bool isPrimary,
        CancellationToken ct)
    {
        var shouldBePrimary = isPrimary || product.Images.Count == 0;
        if (shouldBePrimary)
        {
            foreach (var existing in product.Images)
            {
                existing.IsPrimary = false;
            }
        }

        var nextOrder = product.Images.Count == 0 ? 1 : product.Images.Max(i => i.DisplayOrder) + 1;
        var image = new ProductImage(
            product.Id,
            storagePath,
            publicUrl,
            string.IsNullOrWhiteSpace(altText) ? product.Name : altText,
            nextOrder,
            shouldBePrimary);

        product.Images.Add(image);
        await _repository.UpdateProductAsync(product, ct);

        return new ProductImageDto(
            image.Id,
            image.ProductId,
            image.PublicUrl,
            image.AltText,
            image.DisplayOrder,
            image.IsPrimary);
    }

    public async Task SetPrimaryProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var target = product.Images.FirstOrDefault(i => i.Id == imageId)
            ?? throw new DomainException("Fotografia não encontrada neste produto.");

        foreach (var img in product.Images)
        {
            img.IsPrimary = img.Id == target.Id;
        }

        await _repository.UpdateProductAsync(product, ct);
    }

    public async Task DeleteProductImageAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var product = await _repository.GetProductByIdAsync(productId, ct)
            ?? throw new DomainException("Produto não encontrado.");

        var target = product.Images.FirstOrDefault(i => i.Id == imageId)
            ?? throw new DomainException("Fotografia não encontrada neste produto.");

        product.Images.Remove(target);
        if (target.IsPrimary && product.Images.Count > 0)
        {
            product.Images.OrderBy(i => i.DisplayOrder).First().IsPrimary = true;
        }

        await _repository.UpdateProductAsync(product, ct);
        await _imageStorage.DeleteImageAsync(target.StoragePath, ct);
    }
}

