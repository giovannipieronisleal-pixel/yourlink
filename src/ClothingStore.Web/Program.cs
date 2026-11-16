using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ClothingStore.Application.Interfaces;
using ClothingStore.Application.Services;
using ClothingStore.Domain.Entities;
using ClothingStore.Domain.Enums;
using ClothingStore.Domain.Exceptions;
using ClothingStore.Infrastructure.Persistence;
using ClothingStore.Infrastructure.Security;
using ClothingStore.Infrastructure.Storage;
using ClothingStore.Shared.Clients;
using ClothingStore.Shared.DTOs;
using ClothingStore.Shared.Helpers;
using ClothingStore.Web.Components;
using ClothingStore.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

var ptBrCulture = new System.Globalization.CultureInfo("pt-BR");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ptBrCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ptBrCulture;

var cloudPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(cloudPort) && int.TryParse(cloudPort, out var parsedPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{parsedPort}");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRequestDecompression();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "image/svg+xml",
        "application/manifest+json",
        "application/json",
        "application/ld+json",
        "application/xml",
        "text/xml",
        "text/plain"
    ]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
    ?? Environment.GetEnvironmentVariable("API_BASE_URL")
    ?? "http://localhost:5180";

builder.Services.AddScoped(sp => new HttpClient(new SocketsHttpHandler
{
    ConnectTimeout = TimeSpan.FromMilliseconds(300)
})
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromMilliseconds(800)
});
builder.Services.AddScoped<CatalogApiClient>();

var sharedDbPath = ResolveDatabasePath(builder.Configuration, builder.Environment.ContentRootPath);
var appDataRoot = Path.GetDirectoryName(sharedDbPath) ?? builder.Environment.ContentRootPath;
var outboxPath = Path.Combine(appDataRoot, "email_outbox.txt");

var cloudMirror = CloudAppDataMirrorRegistry.ConfigureFromEnvironment(appDataRoot, builder.Configuration);
if (cloudMirror is not null)
{
    try
    {
        using var hydrateCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await cloudMirror.HydrateToDiskAsync(hydrateCts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[CloudAppDataMirror] Falha na hidratação inicial ({cloudMirror.ProviderName}): {ex.Message}");
    }
    builder.Services.AddSingleton(cloudMirror);
}

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Muitas tentativas em sequência. Aguarde um minuto e tente novamente." },
            token);
    };

    options.AddPolicy("giftcard-strict", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IEmailService>(sp =>
    new SmtpEmailService(sp.GetRequiredService<IConfiguration>(), outboxPath));
builder.Services.AddSingleton<IClothingStoreRepository>(sp =>
    new ClothingStoreRepository(sp.GetRequiredService<IPasswordHasher>(), sharedDbPath));
builder.Services.AddSingleton(sp =>
    new CloudAccountRegistryService(appDataRoot, sp.GetRequiredService<IPasswordHasher>()));
builder.Services.AddSingleton<IMultiTenantStoreResolver>(sp =>
    new MultiTenantStoreManager(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IPasswordHasher>(),
        Path.GetDirectoryName(sharedDbPath)));
builder.Services.AddSingleton<IPlatformMercadoPagoTokenStore>(_ =>
    new PlatformMercadoPagoTokenStore(Path.GetDirectoryName(sharedDbPath)));
builder.Services.AddSingleton<IMercadoPagoPixService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var mpHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient("MercadoPago");
    return new MercadoPagoPixService(
        mpHttp,
        () => MercadoPagoPixService.ReadFromConfig(
            config["MercadoPago:ClientId"],
            config["MercadoPago:ClientSecret"],
            config["MercadoPago:WebhookSecret"]));
});
builder.Services.AddSingleton<IImageStorageService>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var config = sp.GetRequiredService<IConfiguration>();
    var webRoot = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
    return new SupabaseImageStorageService(httpClient, config, webRoot);
});

builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ProductManagementService>();
builder.Services.AddScoped<EmployeeAndSettingsService>();
builder.Services.AddScoped<CloudSyncStateService>();
builder.Services.AddScoped<OrderAndShippingService>(sp =>
    new OrderAndShippingService(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<IMercadoPagoPixService>()));
builder.Services.AddScoped<MultiTenantGatewayService>(sp =>
    new MultiTenantGatewayService(
        sp.GetRequiredService<IMultiTenantStoreResolver>(),
        sp.GetRequiredService<IMercadoPagoPixService>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<IPasswordHasher>(),
        sp.GetRequiredService<IImageStorageService>(),
        sp.GetService<IEmailService>(),
        sp.GetRequiredService<IPlatformMercadoPagoTokenStore>()));
builder.Services.AddScoped<PlatformSubscriptionService>(sp =>
    new PlatformSubscriptionService(
        sp.GetRequiredService<IClothingStoreRepository>(),
        sp.GetRequiredService<IMercadoPagoPixService>(),
        sp.GetRequiredService<IPlatformMercadoPagoTokenStore>()));
builder.Services.AddScoped<CatalogDataGateway>();
builder.Services.AddScoped<PlatformPortalService>();
builder.Services.AddScoped<StoreCartService>();

var app = builder.Build();
if (cloudMirror is not null)
{
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try { cloudMirror.Dispose(); } catch { }
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    await next();
});

app.UseRequestDecompression();
app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        if (string.Equals(path, "/sw.js", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
            return;
        }

        if (path.StartsWith("/icons/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=604800";
        }
    }
});
app.UseAntiforgery();
app.UseRateLimiter();

app.MapMethods("/health", new[] { "GET", "HEAD" }, () => Results.Ok(new
{
    status = "healthy",
    service = "ClothingStore.Web",
    persistence = cloudMirror?.ProviderName ?? "LocalDisk",
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/robots.txt", (HttpContext httpContext) =>
{
    var origin = GetPublicOrigin(httpContext).TrimEnd('/');
    var sb = new StringBuilder();
    sb.AppendLine("User-agent: *");
    sb.AppendLine("Allow: /");
    sb.AppendLine("Disallow: /api/");
    if (!string.IsNullOrWhiteSpace(origin))
    {
        sb.AppendLine($"Sitemap: {origin}/sitemap.xml");
    }

    httpContext.Response.Headers.CacheControl = "public, max-age=300";
    return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
});

app.MapGet("/sitemap.xml", async (
    HttpContext httpContext,
    MultiTenantGatewayService gateway,
    PlatformPortalService portalService,
    CancellationToken ct) =>
{
    var origin = GetPublicOrigin(httpContext).TrimEnd('/');
    if (string.IsNullOrWhiteSpace(origin))
        origin = AppUpdateCatalog.DefaultCentralPlatformServerUrl;

    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

    static void AppendUrl(StringBuilder target, string loc, string changefreq, string priority, DateTimeOffset? lastMod = null)
    {
        target.Append("  <url><loc>").Append(SecurityElement.Escape(loc)).Append("</loc>");
        if (lastMod.HasValue)
            target.Append("<lastmod>").Append(lastMod.Value.ToUniversalTime().ToString("yyyy-MM-dd")).Append("</lastmod>");
        target.Append("<changefreq>").Append(changefreq).Append("</changefreq>");
        target.Append("<priority>").Append(priority).Append("</priority></url>").AppendLine();
    }

    AppendUrl(sb, $"{origin}/", "daily", "1.0");
    AppendUrl(sb, $"{origin}/como-instalar", "weekly", "0.85");
    AppendUrl(sb, $"{origin}/privacidade", "monthly", "0.6");
    AppendUrl(sb, $"{origin}/termos", "monthly", "0.6");

    var isPortalHome = false;
    try { isPortalHome = await portalService.IsPortalHomeAsync(null, ct); } catch { }

    if (!isPortalHome)
    {
        try
        {
            var rootRepo = await gateway.ResolveReadRepositoryAsync(null, ct);
            if (rootRepo is not null)
            {
                var catService = gateway.CreateCatalogService(rootRepo);
                var settings = await catService.GetPublicStoreSettingsAsync(ct);
                if (settings.IsSiteActive)
                {
                    AppendUrl(sb, $"{origin}/catalogo", "daily", "0.9", settings.UpdatedAt);
                    var products = await catService.GetCatalogProductsAsync(new CatalogQueryFilter(), ct);
                    foreach (var p in products.Take(250))
                    {
                        AppendUrl(sb, $"{origin}/produto/{Uri.EscapeDataString(p.Slug)}", "weekly", "0.8");
                    }
                }
            }
        }
        catch { }
    }

    try
    {
        var slugs = await gateway.StoreResolver.ListTenantSlugsAsync(ct);
        foreach (var slug in slugs)
        {
            if (string.IsNullOrWhiteSpace(slug) || string.Equals(slug, "__root__", StringComparison.OrdinalIgnoreCase))
                continue;

            var repo = await gateway.ResolveReadRepositoryAsync(slug, ct);
            if (repo is null)
                continue;

            var catService = gateway.CreateCatalogService(repo);
            var settings = await catService.GetPublicStoreSettingsAsync(ct);
            if (!settings.IsSiteActive)
                continue;

            var encodedSlug = Uri.EscapeDataString(slug);
            AppendUrl(sb, $"{origin}/loja/{encodedSlug}", "daily", "0.9", settings.UpdatedAt);
            AppendUrl(sb, $"{origin}/loja/{encodedSlug}/catalogo", "daily", "0.85", settings.UpdatedAt);

            var products = await catService.GetCatalogProductsAsync(new CatalogQueryFilter(), ct);
            foreach (var p in products.Take(200))
            {
                AppendUrl(sb, $"{origin}/loja/{encodedSlug}/produto/{Uri.EscapeDataString(p.Slug)}", "weekly", "0.8");
            }
        }
    }
    catch { }

    sb.AppendLine("</urlset>");
    httpContext.Response.Headers.CacheControl = "public, max-age=300";
    return Results.Text(sb.ToString(), "application/xml; charset=utf-8");
});

app.MapGet("/media/product/{productId:guid}", (
    Guid productId,
    HttpContext httpContext,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
    ServePublicProductMediaAsync(null, productId, httpContext, gateway, ct));

app.MapGet("/loja/{storeSlug}/media/product/{productId:guid}", (
    string storeSlug,
    Guid productId,
    HttpContext httpContext,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
    ServePublicProductMediaAsync(storeSlug, productId, httpContext, gateway, ct));

app.MapGet("/manifest.webmanifest", async (
    string? storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    PublicStoreSettingsDto? settings = null;
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repo is not null)
            settings = await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct);
    }
    catch
    {
    }

    return Results.Text(PwaManifestBuilder.BuildJson(settings, storeSlug), "application/manifest+json; charset=utf-8");
});

app.MapGet("/loja/{storeSlug}/manifest.webmanifest", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    PublicStoreSettingsDto? settings = null;
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repo is not null)
            settings = await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct);
    }
    catch
    {
    }

    return Results.Text(PwaManifestBuilder.BuildJson(settings, storeSlug), "application/manifest+json; charset=utf-8");
});

app.MapGet("/api/public/store-info", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveStoreSlug(ctx, null);
    var repo = await gateway.ResolveReadRepositoryAsync(slug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada neste servidor." });
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/store-info", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada neste servidor." });
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

app.MapGet("/api/public/categories", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/categories", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
});

app.MapGet("/api/public/filters", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogFiltersAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/filters", async (
    string storeSlug,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    return repo is null
        ? Results.NotFound(new { error = "Loja não encontrada." })
        : Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogFiltersAsync(ct));
});

app.MapGet("/api/public/products", async (
    HttpContext ctx,
    string? search,
    Guid? categoryId,
    string? categorySlug,
    string? color,
    string? size,
    bool? onlyInStock,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var filter = new CatalogQueryFilter(search, categoryId, categorySlug, color, size, onlyInStock ?? false);
    return Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogProductsAsync(filter, ct));
});

app.MapGet("/api/public/stores/{storeSlug}/products", async (
    string storeSlug,
    string? search,
    Guid? categoryId,
    string? categorySlug,
    string? color,
    string? size,
    bool? onlyInStock,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var filter = new CatalogQueryFilter(search, categoryId, categorySlug, color, size, onlyInStock ?? false);
    return Results.Ok(await gateway.CreateCatalogService(repo).GetCatalogProductsAsync(filter, ct));
});

app.MapGet("/api/public/products/{slugOrId}", async (
    HttpContext ctx,
    string slugOrId,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var product = await gateway.CreateCatalogService(repo).GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null ? Results.Ok(product) : Results.NotFound(new { error = "Produto não encontrado." });
});

app.MapGet("/api/public/stores/{storeSlug}/products/{slugOrId}", async (
    string storeSlug,
    string slugOrId,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    var product = await gateway.CreateCatalogService(repo).GetPublicProductDetailAsync(slugOrId, ct);
    return product is not null ? Results.Ok(product) : Results.NotFound(new { error = "Produto não encontrado." });
});

app.MapGet("/api/public/cep/{cep}", async (
    string cep,
    OrderAndShippingService orderService,
    CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await orderService.LookupCepAsync(cep, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/public/shipping-quote", async (
    HttpContext ctx,
    ShippingQuoteRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
        if (repo is null)
            return Results.NotFound(new { error = "Loja não encontrada." });
        return Results.Ok(await gateway.CreateOrderService(repo).QuoteShippingForBuyerAsync(request, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/public/stores/{storeSlug}/shipping-quote", async (
    string storeSlug,
    ShippingQuoteRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
        if (repo is null)
            return Results.NotFound(new { error = "Loja não encontrada." });
        return Results.Ok(await gateway.CreateOrderService(repo).QuoteShippingForBuyerAsync(request, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/public/orders/pix", async (
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        request.ExpressMobileMode = false;
        var created = await gateway.PlacePixOrderForStoreAsync(
            ResolveStoreSlug(ctx, null),
            request,
            GetPublicOrigin(ctx),
            ct);
        return Results.Created($"/api/public/orders/{created.Id}/status", created);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/public/stores/{storeSlug}/orders/pix", async (
    string storeSlug,
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        request.ExpressMobileMode = false;
        var created = await gateway.PlacePixOrderForStoreAsync(
            storeSlug,
            request,
            GetPublicOrigin(ctx),
            ct);
        return Results.Created($"/api/public/stores/{storeSlug}/orders/{created.Id}/status", created);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/public/orders/{orderId:guid}/status", async (
    Guid orderId,
    string? token,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var status = await gateway.GetPublicOrderStatusAsync(ResolveStoreSlug(ctx, null), orderId, token, ct);
        return status is null ? Results.NotFound(new { error = "Pedido não encontrado." }) : Results.Ok(status);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/public/stores/{storeSlug}/orders/{orderId:guid}/status", async (
    string storeSlug,
    Guid orderId,
    string? token,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var status = await gateway.GetPublicOrderStatusAsync(storeSlug, orderId, token, ct);
        return status is null ? Results.NotFound(new { error = "Pedido não encontrado." }) : Results.Ok(status);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapMethods("/api/public/webhooks/mercadopago", ["POST", "GET"], async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
    await HandleMercadoPagoWebhookAsync(ctx, null, gateway, subscriptionService, ct)).DisableAntiforgery();

app.MapMethods("/api/public/webhooks/mercadopago/platform", ["POST", "GET"], async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
    await HandlePlatformMercadoPagoWebhookAsync(ctx, gateway.MercadoPago, subscriptionService, ct)).DisableAntiforgery();

app.MapMethods("/api/public/webhooks/mercadopago/{storeSlug}", ["POST", "GET"], async (
    string storeSlug,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
    await HandleMercadoPagoWebhookAsync(ctx, storeSlug, gateway, subscriptionService, ct)).DisableAntiforgery();

app.MapGet("/api/platform-sync/plan-catalog", async (
    HttpContext ctx,
    IClothingStoreRepository repository,
    IMercadoPagoPixService mercadoPago,
    IPlatformMercadoPagoTokenStore tokenStore,
    CancellationToken ct) =>
{
    var settings = await repository.GetStoreSettingsAsync(ct);
    var isAppConfigured = mercadoPago.GetConfiguredApp().IsOAuthConfigured;
    var conn = await tokenStore.GetConnectionStatusAsync(isAppConfigured, ct);
    var resolvedCentral = WhatsAppLinkBuilder.NormalizePublicServerUrl(settings.CentralCatalogBaseUrl);
    if (string.IsNullOrWhiteSpace(resolvedCentral) || StoreSettings.IsPlaceholderStoreUrl(resolvedCentral))
    {
        resolvedCentral = WhatsAppLinkBuilder.NormalizePublicServerUrl(GetPublicOrigin(ctx));
    }

    var resolvedSupportWhatsapp = WhatsAppLinkBuilder.SanitizeSupportPhone(
        app.Configuration["YOULINK_SUPPORT_WHATSAPP"]
        ?? Environment.GetEnvironmentVariable("YOULINK_SUPPORT_WHATSAPP")
        ?? settings.PlatformSupportWhatsapp);

    return Results.Ok(new PlatformPlanCatalogDto(
        BasicPlanMonthlyPrice: settings.BasicPlanMonthlyPrice,
        IntermediaryPlanMonthlyPrice: settings.IntermediaryPlanMonthlyPrice,
        PremiumPlanMonthlyPrice: settings.PremiumPlanMonthlyPrice,
        HasOfficialPixKey: !string.IsNullOrWhiteSpace(settings.PicpayPixKey),
        CentralCatalogServerUrl: resolvedCentral,
        HasMercadoPagoAppConfigured: isAppConfigured,
        HasPlatformMercadoPagoConnected: conn.IsConnected,
        PlatformMercadoPagoAccountLabel: conn.AccountLabel,
        SupportWhatsappNumber: resolvedSupportWhatsapp));
});

app.MapPost("/api/platform-sync/plan-orders", async (
    HttpContext ctx,
    CreatePlatformPlanOrderRequest request,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    try
    {
        var created = await subscriptionService.CreatePlanOrderAsync(request, GetPublicOrigin(ctx), ct);
        return Results.Created($"/api/platform-sync/plan-orders/{created.Id}", created);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/platform-sync/plan-orders/status", async (
    string orderNumber,
    string claimToken,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await subscriptionService.GetPlanOrderStatusAsync(orderNumber, claimToken, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/platform-sync/plan-orders", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    return Results.Ok(await subscriptionService.GetAllOrdersAsync(ct));
});

app.MapPost("/api/platform-sync/plan-orders/{id:guid}/approve", async (
    Guid id,
    string? adminName,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    try
    {
        return Results.Ok(await subscriptionService.ApprovePlanOrderAsync(id, adminName ?? "Administrador", null, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/plan-orders/{id:guid}/cancel", async (
    Guid id,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    try
    {
        return Results.Ok(await subscriptionService.CancelPlanOrderAsync(id, ct));
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/owner-pricing", async (
    UpdateOwnerPricingSyncRequest request,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    var updated = await settingsService.SyncOwnerPricingAsync(request, ct);
    return Results.Ok(updated);
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/mercadopago/oauth/start", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct))
        return Results.Unauthorized();

    try
    {
        var callbackUri = $"{GetPublicOrigin(ctx).TrimEnd('/')}/api/payments/mercadopago/oauth/callback";
        var resp = gateway.StartPlatformOwnerMercadoPagoOAuth(callbackUri);
        return Results.Ok(resp);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/mercadopago/disconnect", async (
    HttpContext ctx,
    IClothingStoreRepository repository,
    IPlatformMercadoPagoTokenStore tokenStore,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(ctx, repository, ct))
        return Results.Unauthorized();

    await tokenStore.ClearOAuthTokensAsync(ct);
    return Results.Ok(new { disconnected = true });
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/gift-cards/validate", async (
    PlatformGiftCardRedeemRequest request,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    var validation = await settingsService.ValidateGiftCardForRemoteStoreAsync(request, ct);
    return Results.Ok(validation);
}).DisableAntiforgery().RequireRateLimiting("giftcard-strict");

app.MapPost("/api/platform-sync/gift-cards/redeem", async (
    PlatformGiftCardRedeemRequest request,
    HttpContext httpContext,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    try
    {
        var result = await settingsService.RedeemGiftCardForRemoteStoreAsync(request, ct);
        await TryFlushCloudMirrorAsync(httpContext, ct);
        return Results.Ok(result);
    }
    catch (DomainException ex)
    {
        return Results.BadRequest(new
        {
            error = ex.Message,
            codeStatus = GiftCardErrorClassifier.ClassifyMessage(ex.Message)
        });
    }
}).DisableAntiforgery().RequireRateLimiting("giftcard-strict");

app.MapGet("/api/platform-sync/gift-cards/status", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Json(
            new { error = "Chave de sincronização (X-YouLink-Sync-Key) inválida ou ausente neste servidor central." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(await settingsService.GetGiftCardSyncStatusAsync(GetPublicOrigin(httpContext), ct));
});

app.MapGet("/api/platform-sync/gift-cards", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Json(
            new { error = "Chave de sincronização (X-YouLink-Sync-Key) inválida ou ausente." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(await settingsService.GetActivationCodesForSyncAsync(ct));
});

app.MapPost("/api/platform-sync/gift-cards/sync", async (
    SyncPlatformGiftCardsRequest request,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    EmployeeAndSettingsService settingsService,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Json(
            new { error = "Chave de sincronização (X-YouLink-Sync-Key) inválida ou ausente." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var merged = await settingsService.SyncPlatformGiftCardsAsync(request.GiftCards, ct);
    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(merged);
}).DisableAntiforgery();

app.MapGet("/api/platform-sync/accounts", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Json(
            new { error = "Chave de sincronização (X-YouLink-Sync-Key) inválida ou ausente." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var envelope = await BuildPlatformAccountsSyncEnvelopeAsync(
        repository,
        gateway,
        accountRegistry,
        GetPublicOrigin(httpContext),
        ct);
    return Results.Ok(envelope);
});

app.MapPost("/api/platform-sync/apply-vip", async (
    HttpContext httpContext,
    RemoteApplyVipRequest request,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Json(
            new { error = "Chave de sincronização (X-YouLink-Sync-Key) inválida ou ausente." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var cloudMatch = await accountRegistry.ApplyPlanAsync(
        request.TargetEmail,
        request.TargetUsername,
        request.StoreName,
        request.PlanTier,
        request.DurationDays,
        request.ExtendExistingDuration,
        request.ActivateSite,
        ct);

    var targetRepo = await ResolvePlatformSyncTargetRepositoryAsync(repository, gateway, request, cloudMatch, ct);
    if (targetRepo is not null)
    {
        var settings = await targetRepo.GetStoreSettingsAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var normalizedTier = StoreSettings.NormalizePlanTier(request.PlanTier);
        DateTimeOffset? nextExpiry = null;

        if (!string.Equals(normalizedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) &&
            request.DurationDays.HasValue && request.DurationDays.Value > 0)
        {
            var baseStart = request.ExtendExistingDuration &&
                            string.Equals(settings.PlanTier, normalizedTier, StringComparison.OrdinalIgnoreCase) &&
                            settings.PlanExpiresAt.HasValue &&
                            settings.PlanExpiresAt.Value > now
                ? settings.PlanExpiresAt.Value
                : now;
            nextExpiry = baseStart.AddDays(request.DurationDays.Value);
        }

        settings.UpdatePlanAndDesign(normalizedTier, designConfig: settings.DesignConfig);
        settings.PlanExpiresAt = nextExpiry;
        if (request.ActivateSite)
            settings.ActivateSite();
        await targetRepo.UpdateStoreSettingsAsync(settings, ct);
        await TryFlushCloudMirrorAsync(httpContext, ct);

        return Results.Ok(EmployeeAndSettingsService.MapToAdminSettingsDto(settings));
    }

    if (cloudMatch is not null)
    {
        await TryFlushCloudMirrorAsync(httpContext, ct);
        var virtualSettings = new StoreSettings
        {
            StoreName = string.IsNullOrWhiteSpace(cloudMatch.StoreName) ? request.StoreName ?? "Loja YouLink" : cloudMatch.StoreName,
            StoreSlug = string.IsNullOrWhiteSpace(cloudMatch.StoreSlug) ? string.Empty : cloudMatch.StoreSlug
        };
        virtualSettings.UpdatePlanAndDesign(
            StoreSettings.NormalizePlanTier(cloudMatch.PlanTier),
            designConfig: virtualSettings.DesignConfig);
        virtualSettings.PlanExpiresAt = cloudMatch.PlanExpiresAt;
        if (cloudMatch.IsSiteActive)
            virtualSettings.ActivateSite();

        return Results.Ok(EmployeeAndSettingsService.MapToAdminSettingsDto(virtualSettings));
    }

    return Results.NotFound(new
    {
        error = "Conta ou loja de destino ainda não encontrada neste servidor central."
    });
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/toggle-user-active/{userId:guid}", async (
    Guid userId,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
    {
        return Results.Json(
            new { error = "Chave de sincronização (X-YouLink-Sync-Key) inválida ou ausente." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await ToggleUserActiveAcrossRepositoriesAsync(userId, repository, gateway, ct);
    if (result is null)
        return Results.NotFound(new { error = "Usuário não encontrado no servidor." });

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(result);
}).DisableAntiforgery();

app.MapGet("/planos/retorno", async (
    string? orderNumber,
    string? status,
    IClothingStoreRepository repository,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct) =>
{
    var isApproved = string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase);
    var planTitle = "Plano YouLink";
    var message = isApproved
        ? "Recebemos a confirmação inicial do Mercado Pago. Volte ao aplicativo YouLink para concluir a ativação."
        : "Sua solicitação foi enviada ao Mercado Pago. Assim que o pagamento for aprovado, seu plano libera automaticamente no YouLink.";

    if (!string.IsNullOrWhiteSpace(orderNumber))
    {
        var order = await repository.GetPlatformSubscriptionOrderByNumberAsync(orderNumber.Trim(), ct);
        if (order is not null)
        {
            planTitle = order.PlanTier;
            var refreshed = await subscriptionService.GetPlanOrderStatusAsync(order.OrderNumber, order.ClaimToken, ct);
            isApproved = refreshed.IsApproved;
            message = refreshed.Message;
        }
    }

    var html = MercadoPagoPixService.BuildSubscriptionReturnHtml(isApproved, orderNumber, planTitle, message);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/api/cloud/status", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveStoreSlug(ctx, null);
    var repository = await gateway.ResolveReadRepositoryAsync(slug, ct) ?? gateway.StoreResolver.DefaultRepository;
    var settings = await repository.GetStoreSettingsAsync(ct);
    var categories = await repository.GetCategoriesAsync(onlyActive: false, ct);
    var products = await repository.GetProductsAsync(includeArchived: false, ct);
    var hasEnvSecret = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"));
    return Results.Ok(new
    {
        status = "online",
        provider = settings.OwnServerProvider,
        hostingMode = settings.ServerHostingMode,
        storeName = settings.StoreName,
        storeSlug = settings.StoreSlug,
        isSiteActive = settings.IsSiteActive,
        categoriesCount = categories.Count,
        productsCount = products.Count,
        service = "ClothingStore.Web",
        appVersion = AppUpdateCatalog.CurrentAppVersion,
        requiresSyncKey = hasEnvSecret || !string.IsNullOrWhiteSpace(settings.OwnServerSyncSecret),
        multiTenantEnabled = true,
        centralPublishReady = true,
        hasMercadoPagoAppConfigured = gateway.MercadoPago.GetConfiguredApp().IsOAuthConfigured,
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapGet("/api/app/compatibility", (string? clientVersion) => Results.Ok(BuildWebCompatibilityDto(clientVersion)));

app.MapGet("/api/v1/app/compatibility", (string? clientVersion) => Results.Ok(BuildWebCompatibilityDto(clientVersion)));

static ApiCompatibilityStatusDto BuildWebCompatibilityDto(string? clientVersion)
{
    var updateRecommended = AppUpdateCatalog.IsVersionNewer(AppUpdateCatalog.CurrentAppVersion, clientVersion);
    return new ApiCompatibilityStatusDto(
        ApiVersion: "v1",
        MinSupportedAppVersion: "1.0.0",
        LatestAppVersion: AppUpdateCatalog.CurrentAppVersion,
        UpdateRecommended: updateRecommended,
        ClientAppVersion: clientVersion,
        ReleaseUrl: AppUpdateCatalog.ReleasesPageUrl,
        Message: updateRecommended
            ? $"Há uma atualização v{AppUpdateCatalog.CurrentAppVersion} disponível para o YouLink."
            : "Seu aplicativo está atualizado.");
}

app.MapPost("/api/cloud/publish-store", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    try
    {
        var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
        var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                              await IsAuthorizedSyncRequestAsync(httpContext, gateway.StoreResolver.DefaultRepository, ct);

        if (!string.IsNullOrWhiteSpace(providedSyncKey) && !hasValidSyncKey)
            return Results.Unauthorized();

        var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
        var ownerEmail = httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
        var slug = ResolveStoreSlug(httpContext, null);

        var response = await gateway.PublishStoreAsync(
            payload,
            slug,
            providedSyncKey,
            publishToken,
            hasValidSyncKey,
            ownerEmail,
            GetPublicOrigin(httpContext),
            ct);

        if (!response.Success)
            return Results.Conflict(new { error = response.Message });

        await ApplyGrantedCloudAccountVipToPublishedTenantAsync(
            gateway,
            accountRegistry,
            ownerEmail,
            response.StoreSlug,
            payload.Settings.StoreName,
            ct);
        await TryFlushCloudMirrorAsync(httpContext, ct);

        return Results.Ok(response);
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/cloud/orders", async (
    HttpContext httpContext,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(httpContext, gateway.StoreResolver.DefaultRepository, ct);
    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
    var slug = ResolveStoreSlug(httpContext, null);

    var result = await gateway.GetStoreOrdersForSyncAsync(slug, publishToken, hasValidSyncKey, ownerEmail, ct);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
});

app.MapGet("/api/payments/mercadopago/config", (MultiTenantGatewayService gateway) =>
{
    var cfg = gateway.MercadoPago.GetConfiguredApp();
    return Results.Ok(new
    {
        isOAuthConfigured = cfg.IsOAuthConfigured,
        message = cfg.IsOAuthConfigured
            ? "Mercado Pago pronto para conexão em 1 clique."
            : "Configure MP_CLIENT_ID e MP_CLIENT_SECRET nas variáveis do servidor central."
    });
});

app.MapPost("/api/payments/mercadopago/oauth/start", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    try
    {
        var providedSyncKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
        var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                              await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct);
        var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
        var ownerEmail = ctx.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
        var slug = ResolveStoreSlug(ctx, null);
        var callbackUri = $"{GetPublicOrigin(ctx).TrimEnd('/')}/api/payments/mercadopago/oauth/callback";

        var (startResp, issuedToken) = await gateway.StartMercadoPagoOAuthAsync(
            slug, providedSyncKey, publishToken, hasValidSyncKey, ownerEmail, callbackUri, ct);

        return Results.Ok(new
        {
            isConfigured = startResp.IsConfigured,
            authorizationUrl = startResp.AuthorizationUrl,
            stateToken = startResp.StateToken,
            publishToken = issuedToken,
            message = startResp.Message
        });
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapGet("/api/payments/mercadopago/oauth/callback", async (
    string? code,
    string? state,
    string? error,
    string? error_description,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var res = await gateway.CompleteMercadoPagoOAuthCallbackAsync(code, state, error, error_description, ct);
    var html = MercadoPagoPixService.BuildOAuthCallbackHtml(res);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/api/payments/mercadopago/status", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var slug = ResolveStoreSlug(ctx, null);
    return Results.Ok(await gateway.GetMercadoPagoStatusAsync(slug, ct));
});

app.MapPost("/api/payments/mercadopago/disconnect", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var providedSyncKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct);
    var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = ctx.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();
    var slug = ResolveStoreSlug(ctx, null);

    var ok = await gateway.DisconnectMercadoPagoAsync(slug, publishToken, hasValidSyncKey, ownerEmail, ct);
    return ok ? Results.Ok(new { disconnected = true }) : Results.Unauthorized();
}).DisableAntiforgery();

app.MapPost("/api/cloud/sync-state", async (
    HttpContext httpContext,
    CloudCatalogSyncPayload payload,
    IClothingStoreRepository repository,
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(httpContext, repository, ct);
    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var ownerEmail = httpContext.Request.Headers["X-YouLink-Owner-Email"].FirstOrDefault();

    if (!hasValidSyncKey && string.IsNullOrWhiteSpace(publishToken))
    {
        return Results.Unauthorized();
    }

    try
    {
        var result = await gateway.PublishStoreAsync(
            payload,
            ResolveStoreSlug(httpContext, null),
            providedSyncKey,
            publishToken,
            hasValidSyncKey,
            ownerEmail,
            GetPublicOrigin(httpContext),
            ct);

        if (!result.Success)
            return Results.Unauthorized();

        await ApplyGrantedCloudAccountVipToPublishedTenantAsync(
            gateway,
            accountRegistry,
            ownerEmail,
            result.StoreSlug,
            payload.Settings.StoreName,
            ct);
        await TryFlushCloudMirrorAsync(httpContext, ct);

        return Results.Ok(new
        {
            message = "Catálogo online 24h sincronizado com sucesso!",
            syncedAt = DateTimeOffset.UtcNow,
            categoriesCount = result.CategoriesCount,
            productsCount = result.ProductsCount,
            isSiteActive = result.IsSiteActive,
            storeSlug = result.StoreSlug,
            catalogUrl = result.PublicStoreUrl
        });
    }
    catch (ClothingStore.Domain.Exceptions.DomainException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost("/api/cloud/accounts/upsert", async (
    HttpContext httpContext,
    CloudAccountCredentialSyncDto dto,
    CloudAccountRegistryService accountRegistry,
    IClothingStoreRepository defaultRepository,
    CancellationToken ct) =>
{
    var hasValidSyncKey = await IsAuthorizedSyncRequestAsync(httpContext, defaultRepository, ct);
    var res = await accountRegistry.UpsertAccountAsync(dto, hasValidSyncKey, ct);
    return res.Success
        ? Results.Ok(new { success = true, message = res.Message })
        : Results.Json(new { error = res.Message }, statusCode: res.StatusCode);
});

app.MapPost("/api/cloud/accounts/verify-login", async (
    LoginRequest loginRequest,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var res = await accountRegistry.VerifyLoginAsync(loginRequest, ct);
    return Results.Ok(res);
});

app.MapGet("/api/cloud/plan-status", async (
    string? email,
    string? username,
    string? storeSlug,
    string? storeName,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    var fromCloud = await accountRegistry.GetPlanStatusAsync(email, username, storeSlug, storeName, ct);

    var candidateSlug = !string.IsNullOrWhiteSpace(storeSlug)
        ? storeSlug
        : !string.IsNullOrWhiteSpace(fromCloud?.StoreSlug)
            ? fromCloud.StoreSlug
            : storeName;

    if (!string.IsNullOrWhiteSpace(candidateSlug))
    {
        var tenantRepo = await gateway.ResolveReadRepositoryAsync(candidateSlug, ct);
        if (tenantRepo is not null && !ReferenceEquals(tenantRepo, gateway.StoreResolver.DefaultRepository))
        {
            var tenantSettings = await tenantRepo.GetStoreSettingsAsync(ct);
            var tenantTier = tenantSettings.GetEffectivePlanTier(now);
            var cloudTier = fromCloud is not null
                ? CloudAccountRegistryService.ResolveEffectivePlan(fromCloud.PlanTier, fromCloud.PlanExpiresAt, now)
                : StoreSettings.PlanTierBasic;

            if (!string.Equals(tenantTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new CloudPlanStatusDto(
                    PlanTier: tenantTier,
                    PlanExpiresAt: tenantSettings.PlanExpiresAt,
                    IsSiteActive: tenantSettings.IsSiteActive,
                    StoreName: tenantSettings.StoreName,
                    StoreSlug: tenantSettings.StoreSlug));
            }

            if (!string.Equals(cloudTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(fromCloud);
            }

            return Results.Ok(new CloudPlanStatusDto(
                PlanTier: tenantTier,
                PlanExpiresAt: tenantSettings.PlanExpiresAt,
                IsSiteActive: tenantSettings.IsSiteActive,
                StoreName: tenantSettings.StoreName,
                StoreSlug: tenantSettings.StoreSlug));
        }
    }

    if (fromCloud is not null)
        return Results.Ok(fromCloud);

    return Results.Ok(new CloudPlanStatusDto(
        PlanTier: StoreSettings.PlanTierBasic,
        PlanExpiresAt: null,
        IsSiteActive: true,
        StoreName: null,
        StoreSlug: null));
});

app.MapPost("/api/cloud/accounts/purge-all", async (
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    IClothingStoreRepository defaultRepository,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, defaultRepository, ct))
        return Results.Unauthorized();

    var clearedCloudAccounts = await accountRegistry.PurgeAllAccountsAsync(ct);
    var clearedDefaultUsers = defaultRepository is ClothingStoreRepository concrete
        ? await concrete.PurgeAllUserAccountsAsync(alsoClearCatalog: false, ct)
        : 0;

    return Results.Ok(new
    {
        clearedCloudAccounts,
        clearedDefaultUsers,
        message = "Todas as contas de usuários na nuvem foram removidas com segurança."
    });
});

app.Run();

static string ResolveDatabasePath(IConfiguration configuration, string contentRootPath)
{
    var configured = configuration["Database:FilePath"]
        ?? Environment.GetEnvironmentVariable("DATABASE_PATH");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    var parentSrcDir = Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "src"));
    var workspaceCandidate = Directory.Exists(parentSrcDir)
        ? Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "App_Data", "clothingstore_db.json"))
        : null;
    if (!string.IsNullOrWhiteSpace(workspaceCandidate) && EnsureWritableDirectory(workspaceCandidate))
    {
        return workspaceCandidate;
    }

    foreach (var devRoot in new[] { @"C:\Dev\YouLink", @"C:\Dev\appmaui.blazor" })
    {
        if (Directory.Exists(devRoot))
        {
            var devCandidate = Path.Combine(devRoot, "App_Data", "clothingstore_db.json");
            if (EnsureWritableDirectory(devCandidate))
                return devCandidate;
        }
    }

    var candidate = Path.Combine(contentRootPath, "App_Data", "clothingstore_db.json");

    if (EnsureWritableDirectory(candidate))
    {
        return candidate;
    }

    // Contêineres sem permissão de escrita em /app usam diretório temporário gravável
    var tempFallback = Path.Combine(Path.GetTempPath(), "youlink", "App_Data", "clothingstore_db.json");
    EnsureWritableDirectory(tempFallback);
    return tempFallback;
}

static bool EnsureWritableDirectory(string filePath)
{
    try
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            var probeFile = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
        }
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task<bool> IsAuthorizedSyncRequestAsync(
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CancellationToken ct)
{
    var providedKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(providedKey))
    {
        return false;
    }

    var settings = await repository.GetStoreSettingsAsync(ct);
    var expectedKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET"))
        ? Environment.GetEnvironmentVariable("YOULINK_SYNC_SECRET")!
        : settings.OwnServerSyncSecret;

    if (string.IsNullOrWhiteSpace(expectedKey))
    {
        return false;
    }

    var expectedBytes = Encoding.UTF8.GetBytes(expectedKey.Trim());
    var providedBytes = Encoding.UTF8.GetBytes(providedKey.Trim());
    return expectedBytes.Length == providedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}

static string? ResolveStoreSlug(HttpContext ctx, string? routeSlug)
{
    if (!string.IsNullOrWhiteSpace(routeSlug))
        return routeSlug.Trim();
    var querySlug = ctx.Request.Query["storeSlug"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(querySlug))
        return querySlug.Trim();
    var headerSlug = ctx.Request.Headers["X-YouLink-Store-Slug"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(headerSlug) ? null : headerSlug.Trim();
}

static string GetPublicOrigin(HttpContext ctx)
{
    var proto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
    var host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
    var scheme = string.IsNullOrWhiteSpace(proto) ? ctx.Request.Scheme : proto;
    var hostValue = string.IsNullOrWhiteSpace(host) ? ctx.Request.Host.Value : host;
    return string.IsNullOrWhiteSpace(hostValue) ? string.Empty : $"{scheme}://{hostValue}";
}

static async Task<(string RawBody, string? QueryDataId, string? QueryTopic, string? XSignature, string? XRequestId)> ReadWebhookEnvelopeAsync(
    HttpContext ctx,
    CancellationToken ct)
{
    string rawBody = string.Empty;
    if (ctx.Request.Body.CanRead)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        rawBody = await reader.ReadToEndAsync(ct);
    }

    var queryDataId = ctx.Request.Query["data.id"].FirstOrDefault()
                      ?? ctx.Request.Query["id"].FirstOrDefault();
    var queryTopic = ctx.Request.Query["topic"].FirstOrDefault()
                     ?? ctx.Request.Query["type"].FirstOrDefault();
    var xSignature = ctx.Request.Headers["x-signature"].FirstOrDefault();
    var xRequestId = ctx.Request.Headers["x-request-id"].FirstOrDefault();
    return (rawBody, queryDataId, queryTopic, xSignature, xRequestId);
}

static async Task<IResult> HandleMercadoPagoWebhookAsync(
    HttpContext ctx,
    string? storeSlug,
    MultiTenantGatewayService gateway,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct)
{
    var (rawBody, queryDataId, queryTopic, xSignature, xRequestId) = await ReadWebhookEnvelopeAsync(ctx, ct);

    var res = await gateway.ProcessMercadoPagoWebhookAsync(
        storeSlug, queryDataId, queryTopic, xSignature, xRequestId, rawBody, ct);

    if (res.IsUnauthorized)
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(storeSlug) && !res.OrderConfirmed)
    {
        var (dataId, topic) = MultiTenantGatewayService.ExtractPaymentNotification(queryDataId, queryTopic, rawBody);
        var platformRes = await subscriptionService.HandleMercadoPagoWebhookAsync(topic, dataId, ct);
        if (platformRes.Handled)
        {
            return Results.Ok(new
            {
                received = true,
                confirmed = platformRes.OrderApproved,
                message = platformRes.Message
            });
        }
    }

    return Results.Ok(new
    {
        received = true,
        confirmed = res.OrderConfirmed,
        message = res.Message
    });
}

static async Task<IResult> HandlePlatformMercadoPagoWebhookAsync(
    HttpContext ctx,
    IMercadoPagoPixService mercadoPago,
    PlatformSubscriptionService subscriptionService,
    CancellationToken ct)
{
    var (rawBody, queryDataId, queryTopic, xSignature, xRequestId) = await ReadWebhookEnvelopeAsync(ctx, ct);
    var (dataId, topic) = MultiTenantGatewayService.ExtractPaymentNotification(queryDataId, queryTopic, rawBody);

    if (!mercadoPago.VerifyWebhookSignature(null, xSignature, xRequestId, queryDataId ?? dataId))
        return Results.Unauthorized();

    var res = await subscriptionService.HandleMercadoPagoWebhookAsync(topic, dataId, ct);
    return Results.Ok(new
    {
        received = true,
        handled = res.Handled,
        confirmed = res.OrderApproved,
        message = res.Message
    });
}

static async Task TryFlushCloudMirrorAsync(HttpContext ctx, CancellationToken ct)
{
    var mirror = ctx.RequestServices.GetService<CloudAppDataMirror>();
    if (mirror is null)
        return;

    try
    {
        await mirror.FlushAsync(TimeSpan.FromSeconds(3), ct);
    }
    catch
    {
    }
}

static async Task<PlatformAccountsSyncEnvelopeDto> BuildPlatformAccountsSyncEnvelopeAsync(
    IClothingStoreRepository rootRepository,
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    string publicOrigin,
    CancellationToken ct)
{
    var rootSettings = await rootRepository.GetStoreSettingsAsync(ct);
    var rootEffectiveTier = rootSettings.GetEffectivePlanTier();
    var rootPublicUrl = ResolveCentralStorePublicUrl(rootSettings.OnlineBaseUrl, null, publicOrigin);

    var accounts = new List<PlatformAccountSyncDto>();
    var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var tenantBySlug = new Dictionary<string, (StoreSettings Settings, string? PublicUrl, string EffectiveTier, string? OwnerEmail)>(StringComparer.OrdinalIgnoreCase);

    await AppendRepositoryAccountsAsync(
        rootRepository,
        rootSettings,
        rootPublicUrl,
        rootEffectiveTier,
        accounts,
        seenEmails,
        seenUsernames,
        ct);

    var tenantSlugs = await gateway.StoreResolver.ListTenantSlugsAsync(ct);
    foreach (var slug in tenantSlugs)
    {
        if (string.IsNullOrWhiteSpace(slug) || string.Equals(slug, "__root__", StringComparison.OrdinalIgnoreCase))
            continue;

        var tenantRepo = await gateway.StoreResolver.ResolveReadRepositoryAsync(slug, ct);
        if (tenantRepo is null || ReferenceEquals(tenantRepo, rootRepository))
            continue;

        var tenantSettings = await tenantRepo.GetStoreSettingsAsync(ct);
        var tenantTier = tenantSettings.GetEffectivePlanTier();
        var tenantUrl = ResolveCentralStorePublicUrl(tenantSettings.OnlineBaseUrl, slug, publicOrigin);
        var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(slug, ct);

        tenantBySlug[slug] = (tenantSettings, tenantUrl, tenantTier, creds?.OwnerEmail);

        await AppendRepositoryAccountsAsync(
            tenantRepo,
            tenantSettings,
            tenantUrl,
            tenantTier,
            accounts,
            seenEmails,
            seenUsernames,
            ct);
    }

    var cloudAccounts = await accountRegistry.GetAllAccountsAsync(ct);
    foreach (var acc in cloudAccounts)
    {
        var emailKey = acc.Email.Trim();
        var usernameKey = acc.Username.Trim();
        if ((!string.IsNullOrWhiteSpace(emailKey) && seenEmails.Contains(emailKey)) ||
            (!string.IsNullOrWhiteSpace(usernameKey) && seenUsernames.Contains(usernameKey)))
        {
            continue;
        }

        StoreSettings? matchedTenantSettings = null;
        string? matchedUrl = null;
        var nowUtc = DateTimeOffset.UtcNow;
        var accTier = CloudAccountRegistryService.ResolveEffectivePlan(acc.PlanTier, acc.PlanExpiresAt, nowUtc);
        string matchedTier = accTier;
        DateTimeOffset? matchedExpiry = accTier == StoreSettings.PlanTierBasic ? null : acc.PlanExpiresAt;

        if (!string.IsNullOrWhiteSpace(acc.StoreSlug) &&
            tenantBySlug.TryGetValue(acc.StoreSlug, out var bySlug))
        {
            matchedTenantSettings = bySlug.Settings;
            matchedUrl = bySlug.PublicUrl;
            var useCloudOverride = accTier != StoreSettings.PlanTierBasic &&
                (bySlug.EffectiveTier == StoreSettings.PlanTierBasic ||
                 (acc.PlanUpdatedAt.HasValue && acc.PlanUpdatedAt.Value > bySlug.Settings.UpdatedAt));
            matchedTier = useCloudOverride ? accTier : bySlug.EffectiveTier;
            matchedExpiry = useCloudOverride ? matchedExpiry : bySlug.Settings.PlanExpiresAt;
        }
        else if (!string.IsNullOrWhiteSpace(emailKey))
        {
            foreach (var kv in tenantBySlug.Values)
            {
                if (!string.IsNullOrWhiteSpace(kv.OwnerEmail) &&
                    string.Equals(kv.OwnerEmail.Trim(), emailKey, StringComparison.OrdinalIgnoreCase))
                {
                    matchedTenantSettings = kv.Settings;
                    matchedUrl = kv.PublicUrl;
                    var useCloudOverride = accTier != StoreSettings.PlanTierBasic &&
                        (kv.EffectiveTier == StoreSettings.PlanTierBasic ||
                         (acc.PlanUpdatedAt.HasValue && acc.PlanUpdatedAt.Value > kv.Settings.UpdatedAt));
                    matchedTier = useCloudOverride ? accTier : kv.EffectiveTier;
                    matchedExpiry = useCloudOverride ? matchedExpiry : kv.Settings.PlanExpiresAt;
                    break;
                }
            }
        }

        if (matchedUrl is null && !string.IsNullOrWhiteSpace(acc.StoreSlug))
        {
            matchedUrl = ResolveCentralStorePublicUrl(null, acc.StoreSlug, publicOrigin);
        }

        if (!string.IsNullOrWhiteSpace(emailKey))
            seenEmails.Add(emailKey);
        if (!string.IsNullOrWhiteSpace(usernameKey))
            seenUsernames.Add(usernameKey);

        accounts.Add(new PlatformAccountSyncDto(
            UserId: Guid.Empty,
            EmployeeId: Guid.Empty,
            FullName: acc.FullName,
            Username: acc.Username,
            Email: acc.Email,
            RoleName: RoleNames.Admin,
            JobTitle: matchedTenantSettings?.StoreName ?? acc.StoreName,
            Phone: acc.Phone ?? matchedTenantSettings?.WhatsappNumber,
            IsActive: matchedTenantSettings?.IsSiteActive ?? true,
            TwoFactorEnabled: false,
            CreatedAt: acc.CreatedAt,
            LastLoginAt: acc.UpdatedAt,
            StoreName: matchedTenantSettings?.StoreName ?? acc.StoreName,
            StoreSlug: matchedTenantSettings?.StoreSlug ?? acc.StoreSlug,
            OnlineBaseUrl: matchedUrl,
            PlanTier: matchedTier,
            PlanExpiresAt: matchedExpiry));
    }

    // Inclui lojas publicadas cujo dono tenha publicado sem registro prévio no CloudAccountRegistry
    foreach (var (slug, info) in tenantBySlug)
    {
        var alreadyListed = accounts.Any(a =>
            string.Equals(a.StoreSlug, slug, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(info.OwnerEmail) &&
             string.Equals(a.Email, info.OwnerEmail, StringComparison.OrdinalIgnoreCase)));

        if (alreadyListed)
            continue;

        var email = info.OwnerEmail;
        var derivedUsername = !string.IsNullOrWhiteSpace(email)
            ? User.DeriveDefaultUsername(email)
            : slug;

        accounts.Add(new PlatformAccountSyncDto(
            UserId: Guid.Empty,
            EmployeeId: Guid.Empty,
            FullName: info.Settings.StoreName,
            Username: derivedUsername,
            Email: email,
            RoleName: "Lojista",
            JobTitle: info.Settings.StoreName,
            Phone: info.Settings.WhatsappNumber,
            IsActive: info.Settings.IsSiteActive,
            TwoFactorEnabled: false,
            CreatedAt: info.Settings.UpdatedAt,
            LastLoginAt: info.Settings.LastOnlineSyncAt,
            StoreName: info.Settings.StoreName,
            StoreSlug: string.IsNullOrWhiteSpace(info.Settings.StoreSlug) ? slug : info.Settings.StoreSlug,
            OnlineBaseUrl: info.PublicUrl,
            PlanTier: info.EffectiveTier,
            PlanExpiresAt: info.Settings.PlanExpiresAt));
    }

    return new PlatformAccountsSyncEnvelopeDto(
        ServerVersion: AppUpdateCatalog.CurrentAppVersion,
        ServerTimeUtc: DateTimeOffset.UtcNow,
        StoreName: rootSettings.StoreName,
        PlanTier: rootEffectiveTier,
        PlanExpiresAt: rootSettings.PlanExpiresAt,
        OnlineBaseUrl: rootPublicUrl,
        Accounts: accounts);
}

static async Task AppendRepositoryAccountsAsync(
    IClothingStoreRepository repository,
    StoreSettings settings,
    string? publicUrl,
    string effectiveTier,
    List<PlatformAccountSyncDto> target,
    HashSet<string> seenEmails,
    HashSet<string> seenUsernames,
    CancellationToken ct)
{
    var employees = await repository.GetEmployeesAsync(ct);
    foreach (var emp in employees)
    {
        var user = emp.User ?? await repository.GetUserByIdAsync(emp.UserId, ct);
        if (user is null)
            continue;

        var username = string.IsNullOrWhiteSpace(user.Username)
            ? User.DeriveDefaultUsername(user.Email)
            : user.Username;
        var email = user.HasEmailAddress ? user.Email : null;

        if ((!string.IsNullOrWhiteSpace(email) && !seenEmails.Add(email.Trim())) ||
            (!string.IsNullOrWhiteSpace(username) && !seenUsernames.Add(username.Trim())))
        {
            continue;
        }

        target.Add(new PlatformAccountSyncDto(
            UserId: user.Id,
            EmployeeId: emp.Id,
            FullName: emp.FullName,
            Username: username,
            Email: email,
            RoleName: user.Role?.Name ?? RoleNames.Employee,
            JobTitle: emp.JobTitle,
            Phone: emp.Phone,
            IsActive: emp.IsActive && user.IsActive,
            TwoFactorEnabled: user.TwoFactorEnabled,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            StoreName: settings.StoreName,
            StoreSlug: settings.StoreSlug,
            OnlineBaseUrl: publicUrl,
            PlanTier: effectiveTier,
            PlanExpiresAt: settings.PlanExpiresAt));
    }
}

static string? ResolveCentralStorePublicUrl(string? onlineBaseUrl, string? tenantSlug, string publicOrigin)
{
    if (!string.IsNullOrWhiteSpace(onlineBaseUrl) && !StoreSettings.IsPlaceholderStoreUrl(onlineBaseUrl))
        return onlineBaseUrl.Trim().TrimEnd('/');

    if (string.IsNullOrWhiteSpace(publicOrigin))
        return null;

    return string.IsNullOrWhiteSpace(tenantSlug)
        ? publicOrigin.TrimEnd('/')
        : WhatsAppLinkBuilder.BuildCentralTenantCatalogUrl(publicOrigin, tenantSlug);
}

static async Task<IClothingStoreRepository?> ResolvePlatformSyncTargetRepositoryAsync(
    IClothingStoreRepository rootRepository,
    MultiTenantGatewayService gateway,
    RemoteApplyVipRequest request,
    CloudAccountSummary? cloudMatch,
    CancellationToken ct)
{
    var targetStoreName = !string.IsNullOrWhiteSpace(request.StoreName)
        ? request.StoreName.Trim()
        : cloudMatch?.StoreName?.Trim();
    var targetEmail = !string.IsNullOrWhiteSpace(request.TargetEmail)
        ? request.TargetEmail.Trim()
        : cloudMatch?.Email?.Trim();
    var targetUsername = !string.IsNullOrWhiteSpace(request.TargetUsername)
        ? request.TargetUsername.Trim().TrimStart('@')
        : cloudMatch?.Username?.Trim();
    var targetSlug = !string.IsNullOrWhiteSpace(cloudMatch?.StoreSlug)
        ? gateway.StoreResolver.NormalizeStoreSlug(cloudMatch.StoreSlug)
        : string.IsNullOrWhiteSpace(targetStoreName)
            ? null
            : gateway.StoreResolver.NormalizeStoreSlug(targetStoreName);

    foreach (var slug in await gateway.StoreResolver.ListTenantSlugsAsync(ct))
    {
        if (string.IsNullOrWhiteSpace(slug) || string.Equals(slug, "__root__", StringComparison.OrdinalIgnoreCase))
            continue;

        var repo = await gateway.StoreResolver.ResolveReadRepositoryAsync(slug, ct);
        if (repo is null || ReferenceEquals(repo, rootRepository))
            continue;

        var settings = await repo.GetStoreSettingsAsync(ct);
        var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(slug, ct);

        var nameMatches = !string.IsNullOrWhiteSpace(targetStoreName) &&
                          (string.Equals(settings.StoreName, targetStoreName, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(slug, targetSlug, StringComparison.OrdinalIgnoreCase));
        var emailMatches = !string.IsNullOrWhiteSpace(targetEmail) &&
                           !string.IsNullOrWhiteSpace(creds?.OwnerEmail) &&
                           string.Equals(creds.OwnerEmail.Trim(), targetEmail, StringComparison.OrdinalIgnoreCase);

        if (nameMatches || emailMatches)
            return repo;

        if (!string.IsNullOrWhiteSpace(targetUsername))
        {
            var emps = await repo.GetEmployeesAsync(ct);
            foreach (var emp in emps)
            {
                var u = emp.User ?? await repo.GetUserByIdAsync(emp.UserId, ct);
                if (u is not null &&
                    (string.Equals(u.Username, targetUsername, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(targetEmail) && u.HasEmailAddress &&
                      string.Equals(u.Email, targetEmail, StringComparison.OrdinalIgnoreCase))))
                {
                    return repo;
                }
            }
        }
    }

    var hasExplicitNonRootTarget = !string.IsNullOrWhiteSpace(targetStoreName) ||
                                   !string.IsNullOrWhiteSpace(targetEmail) ||
                                   !string.IsNullOrWhiteSpace(targetUsername);
    if (!hasExplicitNonRootTarget)
        return rootRepository;

    var rootSettings = await rootRepository.GetStoreSettingsAsync(ct);
    var rootMatchesStore = !string.IsNullOrWhiteSpace(targetStoreName) &&
                           string.Equals(rootSettings.StoreName, targetStoreName, StringComparison.OrdinalIgnoreCase);
    if (rootMatchesStore)
        return rootRepository;

    var rootEmployees = await rootRepository.GetEmployeesAsync(ct);
    foreach (var emp in rootEmployees)
    {
        var u = emp.User ?? await rootRepository.GetUserByIdAsync(emp.UserId, ct);
        if (u is null)
            continue;
        if ((!string.IsNullOrWhiteSpace(targetEmail) && u.HasEmailAddress &&
             string.Equals(u.Email, targetEmail, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(targetUsername) &&
             string.Equals(u.Username, targetUsername, StringComparison.OrdinalIgnoreCase)))
        {
            return rootRepository;
        }
    }

    return null;
}

static async Task ApplyGrantedCloudAccountVipToPublishedTenantAsync(
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    string? ownerEmail,
    string? publishedSlug,
    string? storeName,
    CancellationToken ct)
{
    var granted = await accountRegistry.GetPlanStatusAsync(ownerEmail, null, publishedSlug, storeName, ct);
    if (granted is null)
        return;

    var now = DateTimeOffset.UtcNow;
    var grantedTier = CloudAccountRegistryService.ResolveEffectivePlan(granted.PlanTier, granted.PlanExpiresAt, now);
    if (string.Equals(grantedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
        return;

    var tenantRepo = await gateway.ResolveReadRepositoryAsync(publishedSlug, ct);
    if (tenantRepo is null || ReferenceEquals(tenantRepo, gateway.StoreResolver.DefaultRepository))
        return;

    var settings = await tenantRepo.GetStoreSettingsAsync(ct);
    if (!string.Equals(settings.GetEffectivePlanTier(now), StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
        return;

    settings.UpdatePlanAndDesign(grantedTier, designConfig: settings.DesignConfig);
    settings.PlanActivatedAt ??= now;
    settings.PlanExpiresAt = granted.PlanExpiresAt;
    if (granted.IsSiteActive)
        settings.ActivateSite();
    await tenantRepo.UpdateStoreSettingsAsync(settings, ct);
}

static async Task<RemoteToggleUserActiveResponse?> ToggleUserActiveAcrossRepositoriesAsync(
    Guid userId,
    IClothingStoreRepository rootRepository,
    MultiTenantGatewayService gateway,
    CancellationToken ct)
{
    var repos = new List<IClothingStoreRepository> { rootRepository };
    foreach (var slug in await gateway.StoreResolver.ListTenantSlugsAsync(ct))
    {
        if (string.IsNullOrWhiteSpace(slug) || string.Equals(slug, "__root__", StringComparison.OrdinalIgnoreCase))
            continue;

        var repo = await gateway.StoreResolver.ResolveReadRepositoryAsync(slug, ct);
        if (repo is not null && !ReferenceEquals(repo, rootRepository))
            repos.Add(repo);
    }

    foreach (var repo in repos)
    {
        var user = await repo.GetUserByIdAsync(userId, ct);
        if (user is null)
            continue;

        var employee = await repo.GetEmployeeByUserIdAsync(user.Id, ct);
        if (employee is null)
            continue;

        var nextActive = !(user.IsActive && employee.IsActive);
        user.IsActive = nextActive;
        employee.IsActive = nextActive;
        await repo.UpdateEmployeeAsync(employee, user, ct);

        return new RemoteToggleUserActiveResponse(
            user.Id,
            employee.Id,
            nextActive,
            nextActive ? $"Conta '{employee.FullName}' reativada no servidor." : $"Conta '{employee.FullName}' suspensa no servidor.");
    }

    return null;
}

static async Task<IResult> ServePublicProductMediaAsync(
    string? storeSlug,
    Guid productId,
    HttpContext httpContext,
    MultiTenantGatewayService gateway,
    CancellationToken ct)
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound();

    var catalogService = gateway.CreateCatalogService(repo);
    var settings = await catalogService.GetPublicStoreSettingsAsync(ct);
    if (!settings.IsSiteActive)
        return Results.NotFound();

    var product = await catalogService.GetPublicProductDetailAsync(productId.ToString("D"), ct);
    var rawUrl = product?.Images.FirstOrDefault()?.PublicUrl;
    if (string.IsNullOrWhiteSpace(rawUrl) || !WhatsAppLinkBuilder.IsSafeMediaUrl(rawUrl))
        return Results.NotFound();

    var sanitized = WhatsAppLinkBuilder.SanitizeMediaUrl(rawUrl);
    if (sanitized.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
    {
        var commaIdx = sanitized.IndexOf(',');
        if (commaIdx > 0)
        {
            var header = sanitized[5..commaIdx];
            var base64Part = sanitized[(commaIdx + 1)..];
            var semiIdx = header.IndexOf(';');
            var mime = (semiIdx >= 0 ? header[..semiIdx] : header).Trim().ToLowerInvariant();
            if (mime is "image/jpeg" or "image/jpg" or "image/png" or "image/webp" or "image/gif" &&
                header.Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = Convert.FromBase64String(base64Part);
                    httpContext.Response.Headers.CacheControl = "public, max-age=86400";
                    return Results.Bytes(bytes, mime == "image/jpg" ? "image/jpeg" : mime);
                }
                catch
                {
                    return Results.NotFound();
                }
            }
        }
        return Results.NotFound();
    }

    httpContext.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.Redirect(sanitized);
}
