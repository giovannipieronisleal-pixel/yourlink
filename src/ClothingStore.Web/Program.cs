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
using Microsoft.AspNetCore.HttpOverrides;
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
builder.Services.AddCors(options =>
{
    options.AddPolicy("YouLinkPwaCors", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});
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

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Segurança atrás de proxy (Render / Cloud Run): lê somente o último hop de X-Forwarded-For / X-Forwarded-Proto
    // e NUNCA aceita X-Forwarded-Host arbitrário.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

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
            partitionKey: ResolveRateLimitPartitionIp(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("cloud-accounts", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionIp(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("auth-strict", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionIp(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("public-orders", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionIp(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("chat-messages", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionIp(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("chat-read", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveRateLimitPartitionIp(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
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
builder.Services.AddSingleton(sp =>
    new CloudChatStoreService(appDataRoot, sp.GetRequiredService<IPasswordHasher>()));
builder.Services.AddSingleton(_ => new RemoteConfigAndTelemetryStore(appDataRoot));
builder.Services.AddSingleton(sp =>
    new CloudflareTurnstileService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("CloudflareTurnstile")));
builder.Services.AddHttpClient("EmailOtp", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton(sp =>
    new EmailOtpService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("EmailOtp")));
builder.Services.AddSingleton(sp =>
    new GeminiProductAssistantService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("GeminiAi"),
        () => sp.GetRequiredService<RemoteConfigAndTelemetryStore>().GetServerGeminiApiKey()));
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
        sp.GetRequiredService<IPlatformMercadoPagoTokenStore>(),
        sp.GetService<IMultiTenantStoreResolver>()));
builder.Services.AddScoped<CatalogDataGateway>();
builder.Services.AddScoped<PlatformPortalService>();
builder.Services.AddScoped<StoreCartService>();
builder.Services.AddScoped<StoreBuyerChatStateService>();

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

app.UseForwardedHeaders();

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
app.UseCors("YouLinkPwaCors");
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        if (string.Equals(path, "/sw.js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/painel/sw.js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/painel/index.html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/painel/app.js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/painel/styles.css", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/painel/manifest.webmanifest", StringComparison.OrdinalIgnoreCase))
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

app.MapGet("/painel", () => Results.Redirect("/painel/index.html", permanent: false));
app.MapGet("/painel/", () => Results.Redirect("/painel/index.html", permanent: false));

app.MapGet("/api/platform/pwa-config", (HttpContext httpContext) =>
{
    var origin = GetPublicOrigin(httpContext).TrimEnd('/');
    if (string.IsNullOrWhiteSpace(origin))
        origin = AppUpdateCatalog.DefaultCentralPlatformServerUrl;

    var cloudflarePanelUrl = Environment.GetEnvironmentVariable("YOULINK_CLOUDFLARE_PANEL_URL")?.Trim();
    var panelUrl = !string.IsNullOrWhiteSpace(cloudflarePanelUrl)
        ? cloudflarePanelUrl
        : AppUpdateCatalog.DefaultCloudPanelUrl;

    httpContext.Response.Headers.CacheControl = "no-cache, must-revalidate";
    return Results.Ok(new
    {
        panelUrl,
        apiBase = origin,
        version = AppUpdateCatalog.CurrentAppVersion,
        edgeEnabled = true
    });
}).RequireCors("YouLinkPwaCors");

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
    ctx.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300";
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

app.MapGet("/api/public/stores/{storeSlug}/store-info", async (
    string storeSlug,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(storeSlug, ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada neste servidor." });
    ctx.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300";
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicStoreSettingsAsync(ct));
});

app.MapGet("/api/public/security-config", (
    HttpContext ctx,
    CloudflareTurnstileService turnstile) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=120";
    return Results.Ok(turnstile.GetPublicConfig());
});

app.MapGet("/api/public/categories", async (
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var repo = await gateway.ResolveReadRepositoryAsync(ResolveStoreSlug(ctx, null), ct);
    if (repo is null)
        return Results.NotFound(new { error = "Loja não encontrada." });
    ctx.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300";
    return Results.Ok(await gateway.CreateCatalogService(repo).GetPublicCategoriesAsync(ct));
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
    CloudflareTurnstileService turnstile,
    CancellationToken ct) =>
{
    try
    {
        var (ok, turnstileErr) = await turnstile.VerifyTokenAsync(
            request.TurnstileToken,
            "checkout_pix",
            ctx.Connection.RemoteIpAddress?.ToString(),
            ct);
        if (!ok)
            return Results.Json(new { error = turnstileErr ?? "Verificação anti-bot inválida." }, statusCode: StatusCodes.Status403Forbidden);

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
}).DisableAntiforgery().RequireRateLimiting("public-orders");

app.MapPost("/api/public/stores/{storeSlug}/orders/pix", async (
    string storeSlug,
    HttpContext ctx,
    CreatePixOrderRequest request,
    MultiTenantGatewayService gateway,
    CloudflareTurnstileService turnstile,
    CancellationToken ct) =>
{
    try
    {
        var (ok, turnstileErr) = await turnstile.VerifyTokenAsync(
            request.TurnstileToken,
            "checkout_pix",
            ctx.Connection.RemoteIpAddress?.ToString(),
            ct);
        if (!ok)
            return Results.Json(new { error = turnstileErr ?? "Verificação anti-bot inválida." }, statusCode: StatusCodes.Status403Forbidden);

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
}).DisableAntiforgery().RequireRateLimiting("public-orders");

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
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
    await HandlePlatformMercadoPagoWebhookAsync(ctx, gateway.MercadoPago, subscriptionService, accountRegistry, ct)).DisableAntiforgery();

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
}).DisableAntiforgery().RequireRateLimiting("public-orders");

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

    var applied = await CloudPlatformPlanSyncHelper.ApplyRemoteVipAsync(
        request,
        repository,
        gateway,
        accountRegistry,
        ct);

    if (applied is not null)
    {
        await TryFlushCloudMirrorAsync(httpContext, ct);
        return Results.Ok(applied);
    }

    return Results.NotFound(new
    {
        error = "Conta ou loja de destino ainda não encontrada neste servidor central."
    });
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/verify-client-vip", async (
    HttpContext httpContext,
    RemoteVerifyClientVipRequest request,
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

    var result = await CloudPlatformPlanSyncHelper.VerifyAndRepairClientVipAsync(
        request,
        repository,
        gateway,
        accountRegistry,
        GetPublicOrigin(httpContext),
        ct);

    if (result.AutoRepaired)
    {
        await TryFlushCloudMirrorAsync(httpContext, ct);
    }

    return Results.Ok(result);
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
        var (isOwnerVerified, verifiedOwnerEmail) = await ResolveVerifiedCloudOwnerEmailAsync(httpContext, accountRegistry, ct);
        var slug = ResolveStoreSlug(httpContext, null);

        // Segurança crítica: NUNCA aplica benefícios VIP antes de PublishStoreAsync autorizar a loja,
        // impedindo que um usuário VIP altere o plano de uma loja de terceiro antes da checagem de posse.
        var response = await gateway.PublishStoreAsync(
            payload,
            slug,
            providedSyncKey,
            publishToken,
            hasValidSyncKey,
            verifiedOwnerEmail,
            GetPublicOrigin(httpContext),
            ct,
            isOwnerEmailServerVerified: isOwnerVerified);

        if (!response.Success)
            return Results.Conflict(new { error = response.Message });

        if (isOwnerVerified && !string.IsNullOrWhiteSpace(verifiedOwnerEmail))
        {
            await ApplyGrantedCloudAccountVipToPublishedTenantAsync(
                gateway,
                accountRegistry,
                verifiedOwnerEmail,
                response.StoreSlug,
                payload.Settings.StoreName,
                ct);
        }
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
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var providedSyncKey = httpContext.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(httpContext, gateway.StoreResolver.DefaultRepository, ct);
    var publishToken = httpContext.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var (isOwnerVerified, verifiedOwnerEmail) = await ResolveVerifiedCloudOwnerEmailAsync(httpContext, accountRegistry, ct);
    var slug = ResolveStoreSlug(httpContext, null);

    var result = await gateway.GetStoreOrdersForSyncAsync(
        slug,
        publishToken,
        hasValidSyncKey,
        verifiedOwnerEmail,
        ct,
        isOwnerEmailServerVerified: isOwnerVerified);
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
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    try
    {
        var providedSyncKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
        var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                              await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct);
        var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
        var (isOwnerVerified, verifiedOwnerEmail) = await ResolveVerifiedCloudOwnerEmailAsync(ctx, accountRegistry, ct);
        var slug = ResolveStoreSlug(ctx, null);
        var callbackUri = $"{GetPublicOrigin(ctx).TrimEnd('/')}/api/payments/mercadopago/oauth/callback";

        var (startResp, issuedToken) = await gateway.StartMercadoPagoOAuthAsync(
            slug,
            providedSyncKey,
            publishToken,
            hasValidSyncKey,
            verifiedOwnerEmail,
            callbackUri,
            ct,
            isOwnerEmailServerVerified: isOwnerVerified);

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
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var providedSyncKey = ctx.Request.Headers["X-YouLink-Sync-Key"].FirstOrDefault();
    var hasValidSyncKey = !string.IsNullOrWhiteSpace(providedSyncKey) &&
                          await IsAuthorizedSyncRequestAsync(ctx, gateway.StoreResolver.DefaultRepository, ct);
    var publishToken = ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();
    var (isOwnerVerified, verifiedOwnerEmail) = await ResolveVerifiedCloudOwnerEmailAsync(ctx, accountRegistry, ct);
    var slug = ResolveStoreSlug(ctx, null);

    var ok = await gateway.DisconnectMercadoPagoAsync(
        slug,
        publishToken,
        hasValidSyncKey,
        verifiedOwnerEmail,
        ct,
        isOwnerEmailServerVerified: isOwnerVerified);
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
    var (isOwnerVerified, verifiedOwnerEmail) = await ResolveVerifiedCloudOwnerEmailAsync(httpContext, accountRegistry, ct);

    if (!hasValidSyncKey && string.IsNullOrWhiteSpace(publishToken) && !isOwnerVerified)
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
            verifiedOwnerEmail,
            GetPublicOrigin(httpContext),
            ct,
            isOwnerEmailServerVerified: isOwnerVerified);

        if (!result.Success)
            return Results.Unauthorized();

        if (isOwnerVerified && !string.IsNullOrWhiteSpace(verifiedOwnerEmail))
        {
            await ApplyGrantedCloudAccountVipToPublishedTenantAsync(
                gateway,
                accountRegistry,
                verifiedOwnerEmail,
                result.StoreSlug,
                payload.Settings.StoreName,
                ct);
        }
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
    EmailOtpService emailOtp,
    IClothingStoreRepository defaultRepository,
    CancellationToken ct) =>
{
    var hasValidSyncKey = await IsAuthorizedSyncRequestAsync(httpContext, defaultRepository, ct);
    var callerCloudToken = httpContext.Request.Headers["X-YouLink-Cloud-Token"].FirstOrDefault() ?? dto.CloudSessionToken;
    var emailVerifiedByServer = false;
    if (!string.IsNullOrWhiteSpace(dto.Email) && !string.IsNullOrWhiteSpace(dto.EmailOtpCode))
    {
        var otpRes = emailOtp.VerifyOtp(new VerifyEmailOtpRequest(dto.Email.Trim(), dto.EmailOtpCode.Trim(), "account_register"));
        if (!otpRes.Verified)
        {
            otpRes = emailOtp.VerifyOtp(new VerifyEmailOtpRequest(dto.Email.Trim(), dto.EmailOtpCode.Trim(), "email_change"));
        }
        emailVerifiedByServer = otpRes.Verified;
    }

    var res = await accountRegistry.UpsertAccountAsync(dto, hasValidSyncKey, ct, emailVerifiedByServer, callerCloudToken);
    if (res.Success)
    {
        await TryFlushCloudMirrorAsync(httpContext, ct);
        return Results.Ok(new
        {
            success = true,
            message = res.Message,
            cloudSessionToken = res.CloudSessionToken,
            cloudSessionExpiresAt = res.CloudSessionExpiresAt
        });
    }
    return Results.Json(new
    {
        error = res.Message,
        requiresEmailOtp = res.RequiresEmailVerification
    }, statusCode: res.StatusCode);
}).DisableAntiforgery().RequireRateLimiting("auth-strict");

app.MapPost("/api/cloud/accounts/verify-login", async (
    LoginRequest loginRequest,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var res = await accountRegistry.VerifyLoginAsync(loginRequest, ct);
    return Results.Ok(res);
}).DisableAntiforgery().RequireRateLimiting("auth-strict");

app.MapPost("/api/cloud/accounts/register", async (
    HttpContext httpContext,
    RegisterUserAccountRequest req,
    CloudAccountRegistryService accountRegistry,
    EmailOtpService emailOtp,
    IPasswordHasher passwordHasher,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.FullName) ||
        string.IsNullOrWhiteSpace(req.Email) ||
        !req.Email.Contains('@', StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(req.Password) ||
        req.Password.Trim().Length < CloudAccountRegistryService.MinimumCloudPasswordLength)
    {
        return Results.BadRequest(new
        {
            error = $"Preencha nome completo, e-mail válido e senha com pelo menos {CloudAccountRegistryService.MinimumCloudPasswordLength} caracteres."
        });
    }

    var cleanEmail = req.Email.Trim();
    var cleanUser = string.IsNullOrWhiteSpace(req.Username)
        ? cleanEmail.Split('@')[0]
        : req.Username.Trim();

    if (string.Equals(cleanUser, "admin", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "O usuário 'admin' é reservado. Escolha outro nome de usuário." });
    }

    var existingAccounts = await accountRegistry.GetAllAccountsAsync(ct);
    var matchedExisting = existingAccounts.FirstOrDefault(a =>
        (!string.IsNullOrWhiteSpace(a.Email) && string.Equals(a.Email.Trim(), cleanEmail, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(a.Username) && string.Equals(a.Username.Trim(), cleanUser, StringComparison.OrdinalIgnoreCase)));

    if (matchedExisting is not null && matchedExisting.HasPassword)
    {
        return Results.Conflict(new { error = "Já existe uma conta cadastrada com este e-mail ou usuário. Faça login na aba Entrar." });
    }

    // Segurança crítica (Item A): TODO cadastro exige confirmação do e-mail por código de 6 dígitos
    // antes de criar a conta ou vinculá-la a qualquer loja/plano.
    if (string.IsNullOrWhiteSpace(req.EmailOtpCode))
    {
        var hasPreGrantedPlan = matchedExisting is not null &&
                                !matchedExisting.HasPassword &&
                                !string.IsNullOrWhiteSpace(matchedExisting.PlanTier) &&
                                !string.Equals(StoreSettings.NormalizePlanTier(matchedExisting.PlanTier), StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);

        var sendRes = await emailOtp.SendOtpAsync(
            new SendEmailOtpRequest(
                cleanEmail,
                "account_register",
                hasPreGrantedPlan ? "confirmação de conta com plano YouLink" : "confirmação de cadastro YouLink"),
            ct);

        if (!sendRes.Sent)
        {
            return Results.Json(new
            {
                error = sendRes.Message,
                requiresEmailOtp = true
            }, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(new
        {
            error = hasPreGrantedPlan
                ? "Este e-mail possui um plano pré-liberado. Enviamos um código de verificação de 6 dígitos para o seu e-mail; informe o código para concluir o cadastro."
                : $"Enviamos um código de verificação de 6 dígitos para {sendRes.MaskedEmail}. Digite o código abaixo para confirmar seu e-mail e criar a conta.",
            requiresEmailOtp = true
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    var otpCheck = emailOtp.VerifyOtp(new VerifyEmailOtpRequest(cleanEmail, req.EmailOtpCode.Trim(), "account_register"));
    if (!otpCheck.Verified)
    {
        return Results.Json(new
        {
            error = otpCheck.Message,
            requiresEmailOtp = true
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    const bool emailVerifiedByServer = true;

    var hash = passwordHasher.HashPassword(req.Password);
    var upsert = await accountRegistry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
        FullName: req.FullName.Trim(),
        Username: cleanUser,
        Email: cleanEmail,
        PasswordHash: hash,
        EmailOtpCode: req.EmailOtpCode,
        IsEmailVerified: true),
        hasValidSyncKey: false,
        ct,
        emailVerifiedByServer: emailVerifiedByServer);

    if (!upsert.Success)
        return Results.Json(new { error = upsert.Message, requiresEmailOtp = upsert.RequiresEmailVerification }, statusCode: upsert.StatusCode);

    await TryFlushCloudMirrorAsync(httpContext, ct);
    var verified = await accountRegistry.VerifyLoginAsync(new LoginRequest(cleanEmail, req.Password), ct);
    return Results.Ok(verified);
}).DisableAntiforgery().RequireRateLimiting("auth-strict");

app.MapPost("/api/cloud/pull-store", async (
    HttpContext httpContext,
    CloudPullStoreRequest request,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var auth = await AuthenticateMerchantSessionOrPublishAsync(
        request.CloudSessionToken,
        request.PublishToken,
        request.StoreSlug,
        httpContext,
        accountRegistry,
        gateway,
        ct);

    if (!auth.Verified)
        return Results.Unauthorized();

    var response = await gateway.PullStoreSnapshotForOwnerAsync(
        auth.Email,
        request.StoreSlug,
        auth.StoreSlug,
        ct,
        authorizedByPublishToken: auth.AuthorizedByPublishToken,
        isOwnerEmailVerified: auth.IsEmailVerified);

    return Results.Ok(response);
}).DisableAntiforgery().RequireRateLimiting("auth-strict");

app.MapGet("/api/cloud/email-otp/status", (EmailOtpService emailOtp) =>
    Results.Ok(emailOtp.GetPublicStatus())).RequireCors("YouLinkPwaCors");

app.MapPost("/api/cloud/email-otp/send", async (
    SendEmailOtpRequest request,
    EmailOtpService emailOtp,
    CancellationToken ct) =>
{
    var result = await emailOtp.SendOtpAsync(request, ct);
    return result.Sent
        ? Results.Ok(result)
        : Results.BadRequest(result);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("giftcard-strict");

app.MapPost("/api/cloud/email-otp/verify", (
    VerifyEmailOtpRequest request,
    EmailOtpService emailOtp) =>
{
    var result = emailOtp.VerifyOtp(request);
    return result.Verified
        ? Results.Ok(result)
        : Results.BadRequest(result);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("giftcard-strict");

app.MapPost("/api/cloud/accounts/password-reset/send", async (
    CloudPasswordResetSendRequest request,
    CloudAccountRegistryService accountRegistry,
    EmailOtpService emailOtp,
    CancellationToken ct) =>
{
    var id = (request.Identifier ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest(new SendEmailOtpResponse(
            Sent: false,
            ProviderConfigured: emailOtp.IsConfigured,
            MaskedEmail: string.Empty,
            ExpiresInSeconds: 0,
            ResendAfterSeconds: 0,
            Message: "Informe o seu e-mail ou nome de usuário cadastrado."));
    }

    var allAccounts = await accountRegistry.GetAllAccountsAsync(ct);
    var account = allAccounts.FirstOrDefault(a =>
        (!string.IsNullOrWhiteSpace(a.Email) && string.Equals(a.Email.Trim(), id, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(a.Username) && string.Equals(a.Username.Trim(), id, StringComparison.OrdinalIgnoreCase)));

    if (account is null || string.IsNullOrWhiteSpace(account.Email) || !account.Email.Contains('@', StringComparison.Ordinal))
    {
        return Results.Ok(new SendEmailOtpResponse(
            Sent: true,
            ProviderConfigured: emailOtp.IsConfigured,
            MaskedEmail: EmailOtpService.MaskEmail(id.Contains('@') ? id : "usuario@exemplo.com"),
            ExpiresInSeconds: 600,
            ResendAfterSeconds: 45,
            Message: "Se existir uma conta ativa com este e-mail ou usuário, enviamos um código de 6 dígitos para a caixa de entrada cadastrada."));
    }

    var otpResult = await emailOtp.SendOtpAsync(
        new SendEmailOtpRequest(
            Email: account.Email.Trim(),
            Purpose: "password_reset",
            ContextLabel: "redefinição de senha YouLink"),
        ct);

    return otpResult.Sent
        ? Results.Ok(otpResult)
        : Results.BadRequest(otpResult);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("giftcard-strict");

app.MapPost("/api/cloud/accounts/password-reset/confirm", async (
    HttpContext httpContext,
    CloudPasswordResetConfirmRequest request,
    CloudAccountRegistryService accountRegistry,
    EmailOtpService emailOtp,
    IPasswordHasher passwordHasher,
    CancellationToken ct) =>
{
    var id = (request.Identifier ?? string.Empty).Trim();
    var code = (request.Code ?? string.Empty).Trim();
    var newPassword = request.NewPassword ?? string.Empty;

    if (string.IsNullOrWhiteSpace(id) || code.Length != 6)
    {
        return Results.BadRequest(new CloudPasswordResetConfirmResponse(
            Success: false,
            NormalizedEmail: string.Empty,
            NewPasswordHash: null,
            Message: "Informe seu e-mail/usuário e o código de 6 dígitos recebido no e-mail."));
    }

    if (newPassword.Trim().Length < CloudAccountRegistryService.MinimumCloudPasswordLength)
    {
        return Results.BadRequest(new CloudPasswordResetConfirmResponse(
            Success: false,
            NormalizedEmail: string.Empty,
            NewPasswordHash: null,
            Message: $"A nova senha deve ter no mínimo {CloudAccountRegistryService.MinimumCloudPasswordLength} caracteres."));
    }

    var allAccounts = await accountRegistry.GetAllAccountsAsync(ct);
    var account = allAccounts.FirstOrDefault(a =>
        (!string.IsNullOrWhiteSpace(a.Email) && string.Equals(a.Email.Trim(), id, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(a.Username) && string.Equals(a.Username.Trim(), id, StringComparison.OrdinalIgnoreCase)));

    if (account is null || string.IsNullOrWhiteSpace(account.Email))
    {
        return Results.BadRequest(new CloudPasswordResetConfirmResponse(
            Success: false,
            NormalizedEmail: string.Empty,
            NewPasswordHash: null,
            Message: "Código inválido ou expirado. Solicite um novo código por e-mail."));
    }

    var verify = emailOtp.VerifyOtp(new VerifyEmailOtpRequest(account.Email.Trim(), code, "password_reset"));
    if (!verify.Verified)
    {
        return Results.BadRequest(new CloudPasswordResetConfirmResponse(
            Success: false,
            NormalizedEmail: verify.NormalizedEmail,
            NewPasswordHash: null,
            Message: verify.Message));
    }

    var newHash = passwordHasher.HashPassword(newPassword);
    var upsert = await accountRegistry.UpsertAccountAsync(new CloudAccountCredentialSyncDto(
        FullName: string.IsNullOrWhiteSpace(account.FullName) ? account.Username : account.FullName,
        Username: account.Username,
        Email: account.Email.Trim(),
        PasswordHash: newHash,
        Phone: account.Phone,
        StoreName: account.StoreName,
        StoreSlug: account.StoreSlug,
        IsEmailVerified: true),
        hasValidSyncKey: true,
        ct,
        emailVerifiedByServer: true);

    if (!upsert.Success)
    {
        return Results.Json(new CloudPasswordResetConfirmResponse(
            Success: false,
            NormalizedEmail: account.Email.Trim(),
            NewPasswordHash: null,
            Message: upsert.Message), statusCode: upsert.StatusCode);
    }

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(new CloudPasswordResetConfirmResponse(
        Success: true,
        NormalizedEmail: account.Email.Trim(),
        NewPasswordHash: null,
        Message: "✓ Senha redefinida com sucesso! Agora entre com sua nova senha."));
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("giftcard-strict");

// =====================================================================
// CONTAS DE COMPRADOR & CHAT INTERNO (VITRINE ↔ LOJISTA & LOJISTA ↔ SUPORTE)
// =====================================================================

app.MapPost("/api/cloud/buyers/register", async (
    HttpContext httpContext,
    RegisterBuyerAccountRequest request,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var res = await chatStore.RegisterBuyerAsync(request, ct);
    if (!res.Success)
        return Results.BadRequest(res);

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(res);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("giftcard-strict");

app.MapPost("/api/cloud/buyers/login", async (
    HttpContext httpContext,
    BuyerLoginRequest request,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var res = await chatStore.LoginBuyerAsync(request, ct);
    if (!res.Success)
        return Results.BadRequest(res);

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(res);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("giftcard-strict");

app.MapGet("/api/cloud/buyers/me", async (
    HttpContext httpContext,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var token = httpContext.Request.Headers["X-Buyer-Token"].FirstOrDefault();
    var session = await chatStore.ValidateBuyerTokenAsync(token, ct);
    return session is null ? Results.Unauthorized() : Results.Ok(session);
}).RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-read");

app.MapGet("/api/cloud/store-chat/{storeSlug}/my-thread", async (
    string storeSlug,
    string? storeName,
    HttpContext httpContext,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var token = httpContext.Request.Headers["X-Buyer-Token"].FirstOrDefault();
    var session = await chatStore.ValidateBuyerTokenAsync(token, ct);
    if (session is null)
        return Results.Unauthorized();

    var detail = await chatStore.GetOrCreateBuyerStoreThreadAsync(session.BuyerId, storeSlug, storeName, ct);
    return detail is null ? Results.BadRequest() : Results.Ok(detail);
}).RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-read");

app.MapPost("/api/cloud/store-chat/{storeSlug}/messages", async (
    string storeSlug,
    SendBuyerChatMessageRequest request,
    HttpContext httpContext,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var token = httpContext.Request.Headers["X-Buyer-Token"].FirstOrDefault();
    var session = await chatStore.ValidateBuyerTokenAsync(token, ct);
    if (session is null)
        return Results.Unauthorized();

    var detail = await chatStore.SendBuyerStoreMessageAsync(
        session.BuyerId,
        storeSlug,
        request.StoreName,
        request.Text,
        request.ContextBadge,
        ct);
    if (detail is null)
        return Results.BadRequest(new { error = "Informe uma mensagem válida ou aguarde alguns segundos antes de enviar novamente." });

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-messages");

app.MapPost("/api/cloud/store-chat/{storeSlug}/threads/{threadId:guid}/messages/{messageId:guid}/delete-for-me", async (
    string storeSlug,
    Guid threadId,
    Guid messageId,
    HttpContext httpContext,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var token = httpContext.Request.Headers["X-Buyer-Token"].FirstOrDefault();
    var session = await chatStore.ValidateBuyerTokenAsync(token, ct);
    if (session is null)
        return Results.Unauthorized();

    var deleted = await chatStore.DeleteMessageForActorAsync(
        threadId,
        messageId,
        CloudChatStoreService.BuildBuyerActorKey(session.BuyerId),
        ct);
    if (!deleted)
        return Results.NotFound();

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(new { deleted = true });
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-messages");

app.MapPost("/api/cloud/merchant-chat/threads", async (
    MerchantChatAuthRequest authRequest,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var storeAccess = await AuthorizeMerchantStoreChatAccessAsync(
        authRequest,
        httpContext,
        accountRegistry,
        gateway,
        ct,
        allowEmptyWhenUnpublished: true);
    if (!storeAccess.Authorized || string.IsNullOrWhiteSpace(storeAccess.AuthorizedStoreSlug))
        return storeAccess.FailureResult ?? Results.Unauthorized();

    var threads = await chatStore.GetMerchantStoreThreadsAsync(storeAccess.AuthorizedStoreSlug, ct);
    return Results.Ok(threads);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-read");

app.MapPost("/api/cloud/merchant-chat/threads/{threadId:guid}", async (
    Guid threadId,
    MerchantChatAuthRequest authRequest,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var storeAccess = await AuthorizeMerchantStoreChatAccessAsync(authRequest, httpContext, accountRegistry, gateway, ct);
    if (!storeAccess.Authorized || string.IsNullOrWhiteSpace(storeAccess.AuthorizedStoreSlug))
        return storeAccess.FailureResult ?? Results.Unauthorized();

    var detail = await chatStore.GetMerchantStoreThreadDetailAsync(storeAccess.AuthorizedStoreSlug, threadId, ct);
    if (detail is null)
        return Results.NotFound();

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-read");

app.MapPost("/api/cloud/merchant-chat/threads/{threadId:guid}/send", async (
    Guid threadId,
    MerchantSendChatMessageRequest request,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var storeAccess = await AuthorizeMerchantStoreChatAccessAsync(request.Auth, httpContext, accountRegistry, gateway, ct);
    if (!storeAccess.Authorized || string.IsNullOrWhiteSpace(storeAccess.AuthorizedStoreSlug))
        return storeAccess.FailureResult ?? Results.Unauthorized();

    var detail = await chatStore.SendMerchantStoreReplyAsync(storeAccess.AuthorizedStoreSlug, request.Auth.StoreName, threadId, request.Text, request.ContextBadge, ct);
    if (detail is null)
        return Results.BadRequest(new { error = "Não foi possível enviar a mensagem ou limite de mensagens por minuto atingido." });

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-messages");

app.MapPost("/api/cloud/merchant-chat/threads/{threadId:guid}/messages/{messageId:guid}/delete-for-me", async (
    Guid threadId,
    Guid messageId,
    MerchantChatAuthRequest authRequest,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var storeAccess = await AuthorizeMerchantStoreChatAccessAsync(authRequest, httpContext, accountRegistry, gateway, ct);
    if (!storeAccess.Authorized || string.IsNullOrWhiteSpace(storeAccess.AuthorizedStoreSlug))
        return storeAccess.FailureResult ?? Results.Unauthorized();

    var deleted = await chatStore.DeleteMessageForActorAsync(
        threadId,
        messageId,
        CloudChatStoreService.BuildMerchantStoreActorKey(storeAccess.AuthorizedStoreSlug),
        ct);
    if (!deleted)
        return Results.NotFound();

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(new { deleted = true });
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-messages");

app.MapPost("/api/cloud/support-chat/my-thread", async (
    MerchantChatAuthRequest authRequest,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var auth = await AuthenticateMerchantSessionOrPublishAsync(
        authRequest.CloudSessionToken,
        authRequest.PublishToken,
        authRequest.StoreSlug,
        httpContext,
        accountRegistry,
        gateway,
        ct);
    if (!auth.Verified)
        return Results.Unauthorized();

    var accountKey = !string.IsNullOrWhiteSpace(auth.Email) ? auth.Email! : (!string.IsNullOrWhiteSpace(auth.StoreSlug) ? auth.StoreSlug! : authRequest.StoreSlug);
    var handle = !string.IsNullOrWhiteSpace(authRequest.Username) ? authRequest.Username : accountKey;
    var storeSlug = !string.IsNullOrWhiteSpace(authRequest.StoreSlug) ? authRequest.StoreSlug : (auth.StoreSlug ?? "minha-loja");
    var storeName = !string.IsNullOrWhiteSpace(authRequest.StoreName) ? authRequest.StoreName : storeSlug;

    var detail = await chatStore.GetOrCreateMerchantSupportThreadAsync(accountKey, handle, storeSlug, storeName, ct);
    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-read");

app.MapPost("/api/cloud/support-chat/send", async (
    MerchantSendChatMessageRequest request,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var auth = await AuthenticateMerchantSessionOrPublishAsync(
        request.Auth.CloudSessionToken,
        request.Auth.PublishToken,
        request.Auth.StoreSlug,
        httpContext,
        accountRegistry,
        gateway,
        ct);
    if (!auth.Verified)
        return Results.Unauthorized();

    var accountKey = !string.IsNullOrWhiteSpace(auth.Email) ? auth.Email! : (!string.IsNullOrWhiteSpace(auth.StoreSlug) ? auth.StoreSlug! : request.Auth.StoreSlug);
    var handle = !string.IsNullOrWhiteSpace(request.Auth.Username) ? request.Auth.Username : accountKey;
    var storeSlug = !string.IsNullOrWhiteSpace(request.Auth.StoreSlug) ? request.Auth.StoreSlug : (auth.StoreSlug ?? "minha-loja");
    var storeName = !string.IsNullOrWhiteSpace(request.Auth.StoreName) ? request.Auth.StoreName : storeSlug;

    var detail = await chatStore.SendMerchantSupportMessageAsync(accountKey, handle, storeSlug, storeName, request.Text, request.ContextBadge, ct);
    if (detail is null)
        return Results.BadRequest(new { error = "Informe uma mensagem válida ou aguarde alguns segundos antes de enviar novamente." });

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-messages");

app.MapPost("/api/cloud/support-chat/threads/{threadId:guid}/messages/{messageId:guid}/delete-for-me", async (
    Guid threadId,
    Guid messageId,
    MerchantChatAuthRequest authRequest,
    HttpContext httpContext,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    var auth = await AuthenticateMerchantSessionOrPublishAsync(
        authRequest.CloudSessionToken,
        authRequest.PublishToken,
        authRequest.StoreSlug,
        httpContext,
        accountRegistry,
        gateway,
        ct);
    if (!auth.Verified)
        return Results.Unauthorized();

    var accountKey = !string.IsNullOrWhiteSpace(auth.Email) ? auth.Email! : (!string.IsNullOrWhiteSpace(auth.StoreSlug) ? auth.StoreSlug! : authRequest.StoreSlug);
    var deleted = await chatStore.DeleteMessageForActorAsync(
        threadId,
        messageId,
        CloudChatStoreService.BuildMerchantSupportActorKey(accountKey),
        ct);
    if (!deleted)
        return Results.NotFound();

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(new { deleted = true });
}).DisableAntiforgery().RequireCors("YouLinkPwaCors").RequireRateLimiting("chat-messages");

app.MapGet("/api/platform-sync/support-chat/threads", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    var threads = await chatStore.GetPlatformSupportThreadsAsync(ct);
    return Results.Ok(threads);
});

app.MapGet("/api/platform-sync/support-chat/threads/{threadId:guid}", async (
    Guid threadId,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    var detail = await chatStore.GetPlatformSupportThreadDetailAsync(threadId, ct);
    if (detail is null)
        return Results.NotFound();

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
});

app.MapPost("/api/platform-sync/support-chat/threads/{threadId:guid}/send", async (
    Guid threadId,
    PlatformSupportSendMessageRequest request,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    var detail = await chatStore.SendPlatformSupportReplyAsync(threadId, request.Text, request.SenderLabel, ct);
    if (detail is null)
        return Results.BadRequest(new { error = "Informe uma mensagem válida." });

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(detail);
}).DisableAntiforgery();

app.MapPost("/api/platform-sync/support-chat/threads/{threadId:guid}/messages/{messageId:guid}/delete-for-me", async (
    Guid threadId,
    Guid messageId,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    CloudChatStoreService chatStore,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Unauthorized();

    var deleted = await chatStore.DeleteMessageForActorAsync(
        threadId,
        messageId,
        CloudChatStoreService.PlatformOwnerActorKey,
        ct);
    if (!deleted)
        return Results.NotFound();

    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(new { deleted = true });
}).DisableAntiforgery();

app.MapGet("/api/cloud/plan-status", async (
    string? email,
    string? username,
    string? storeSlug,
    string? storeName,
    string? clientPlanTier,
    DateTimeOffset? clientPlanExpiresAt,
    string? clientAppVersion,
    IClothingStoreRepository rootRepository,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CancellationToken ct) =>
{
    var status = await CloudPlatformPlanSyncHelper.ResolveCloudPlanStatusAsync(
        email,
        username,
        storeSlug,
        storeName,
        clientPlanTier,
        clientPlanExpiresAt,
        clientAppVersion,
        rootRepository,
        gateway,
        accountRegistry,
        ct);
    return Results.Ok(status);
});

app.MapPost("/api/cloud/client-vip-ack", async (
    CloudClientVipAckRequest request,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var recorded = await accountRegistry.RecordClientCheckInAsync(
        request.Email,
        request.Username,
        request.StoreSlug,
        request.StoreName,
        request.AppliedPlanTier,
        request.AppliedExpiresAt,
        request.ClientAppVersion,
        ct);
    return Results.Ok(new { recorded });
}).DisableAntiforgery();

app.MapGet("/api/cloud/check-render-store-link", async (
    string? slug,
    string? ownerEmail,
    string? ownerUsername,
    HttpContext ctx,
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct) =>
{
    var publicOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host}".TrimEnd('/');
    var normalizedSlug = WhatsAppLinkBuilder.GenerateStoreSlug(slug);
    var storeUrl = WhatsAppLinkBuilder.BuildStoreCatalogUrl(publicOrigin, normalizedSlug);

    var reservedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "login", "planos", "site", "loja", "catalogo",
        "produto", "privacidade", "termos", "health", "como-instalar", "swagger"
    };

    var cleanEmail = string.IsNullOrWhiteSpace(ownerEmail) ? null : ownerEmail.Trim();
    var cleanUser = string.IsNullOrWhiteSpace(ownerUsername) ? null : ownerUsername.Trim();

    var takenSlugs = new HashSet<string>(reservedSlugs, StringComparer.OrdinalIgnoreCase);
    var ownedByCaller = false;
    var takenByOther = reservedSlugs.Contains(normalizedSlug);

    var tenantSlugs = await gateway.StoreResolver.ListTenantSlugsAsync(ct);
    foreach (var tSlug in tenantSlugs)
    {
        if (string.IsNullOrWhiteSpace(tSlug) || string.Equals(tSlug, "__root__", StringComparison.OrdinalIgnoreCase))
            continue;

        var normTenant = WhatsAppLinkBuilder.GenerateStoreSlug(tSlug);
        takenSlugs.Add(normTenant);

        if (string.Equals(normTenant, normalizedSlug, StringComparison.OrdinalIgnoreCase))
        {
            var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(tSlug, ct);
            if (!string.IsNullOrWhiteSpace(cleanEmail) &&
                !string.IsNullOrWhiteSpace(creds?.OwnerEmail) &&
                string.Equals(creds.OwnerEmail.Trim(), cleanEmail, StringComparison.OrdinalIgnoreCase))
            {
                ownedByCaller = true;
            }
            else
            {
                takenByOther = true;
            }
        }
    }

    var accounts = await accountRegistry.GetAllAccountsAsync(ct);
    foreach (var acc in accounts)
    {
        if (string.IsNullOrWhiteSpace(acc.StoreSlug) ||
            string.Equals(acc.StoreSlug.Trim(), "minha-loja", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var normAccSlug = WhatsAppLinkBuilder.GenerateStoreSlug(acc.StoreSlug);
        takenSlugs.Add(normAccSlug);

        if (string.Equals(normAccSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase))
        {
            var matchesOwner =
                (!string.IsNullOrWhiteSpace(cleanEmail) &&
                 !string.IsNullOrWhiteSpace(acc.Email) &&
                 string.Equals(acc.Email.Trim(), cleanEmail, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(cleanUser) &&
                 !string.IsNullOrWhiteSpace(acc.Username) &&
                 string.Equals(acc.Username.Trim(), cleanUser, StringComparison.OrdinalIgnoreCase));

            if (matchesOwner)
                ownedByCaller = true;
            else
                takenByOther = true;
        }
    }

    if (ownedByCaller && !reservedSlugs.Contains(normalizedSlug))
    {
        return Results.Ok(new RenderStoreLinkAvailabilityDto(
            NormalizedSlug: normalizedSlug,
            StoreUrl: storeUrl,
            ServerOnline: true,
            IsAvailable: true,
            OwnedByCurrentAccount: true,
            Message: "Este endereço no servidor Render já pertence à sua loja!",
            Suggestions: []));
    }

    if (!takenByOther)
    {
        return Results.Ok(new RenderStoreLinkAvailabilityDto(
            NormalizedSlug: normalizedSlug,
            StoreUrl: storeUrl,
            ServerOnline: true,
            IsAvailable: true,
            OwnedByCurrentAccount: false,
            Message: "Endereço disponível no servidor Render!",
            Suggestions: []));
    }

    var suggestions = new List<string>(4);
    string[] suffixes = ["-oficial", "-loja", "-2", "-br", "-3", "-shop", "-online"];
    foreach (var suffix in suffixes)
    {
        var candidate = WhatsAppLinkBuilder.GenerateStoreSlug($"{normalizedSlug}{suffix}");
        if (!takenSlugs.Contains(candidate) && !suggestions.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            suggestions.Add(candidate);
            if (suggestions.Count >= 4)
                break;
        }
    }

    return Results.Ok(new RenderStoreLinkAvailabilityDto(
        NormalizedSlug: normalizedSlug,
        StoreUrl: storeUrl,
        ServerOnline: true,
        IsAvailable: false,
        OwnedByCurrentAccount: false,
        Message: reservedSlugs.Contains(normalizedSlug)
            ? "Este endereço é reservado pelo sistema. Escolha uma das sugestões livres no Render."
            : "Este endereço já está em uso por outra loja no servidor Render.",
        Suggestions: suggestions));
});

app.MapGet("/api/cloud/remote-config", async (
    HttpContext ctx,
    RemoteConfigAndTelemetryStore store,
    CancellationToken ct) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300";
    return Results.Ok(await store.GetRemoteConfigAsync(ct));
});

app.MapPost("/api/platform-sync/remote-config", async (
    HttpContext httpContext,
    RemoteFeatureConfigDto config,
    IClothingStoreRepository repository,
    RemoteConfigAndTelemetryStore store,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Json(new { error = "Chave de sincronização inválida ou ausente." }, statusCode: StatusCodes.Status401Unauthorized);

    var saved = await store.SaveRemoteConfigAsync(config, ct);
    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(saved);
}).DisableAntiforgery();

app.MapPost("/api/cloud/telemetry/error", async (
    ClientCrashReportRequest report,
    RemoteConfigAndTelemetryStore store,
    CancellationToken ct) =>
{
    var recorded = await store.RecordClientErrorAsync(report, ct);
    return Results.Ok(new { recorded = true, id = recorded.Id });
}).DisableAntiforgery().RequireRateLimiting("cloud-accounts");

app.MapGet("/api/platform-sync/telemetry/errors", async (
    int? limit,
    HttpContext httpContext,
    IClothingStoreRepository repository,
    RemoteConfigAndTelemetryStore store,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Json(new { error = "Chave de sincronização inválida ou ausente." }, statusCode: StatusCodes.Status401Unauthorized);

    return Results.Ok(await store.GetRecentErrorsAsync(limit ?? 50, ct));
});

app.MapGet("/api/platform-sync/ai-config", async (
    HttpContext httpContext,
    IClothingStoreRepository repository,
    RemoteConfigAndTelemetryStore store,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Json(new { error = "Chave de sincronização inválida ou ausente." }, statusCode: StatusCodes.Status401Unauthorized);

    return Results.Ok(store.GetServerAiKeyStatus());
});

app.MapPost("/api/platform-sync/ai-config", async (
    HttpContext httpContext,
    SaveServerAiKeyRequest request,
    IClothingStoreRepository repository,
    RemoteConfigAndTelemetryStore store,
    CancellationToken ct) =>
{
    if (!await IsAuthorizedSyncRequestAsync(httpContext, repository, ct))
        return Results.Json(new { error = "Chave de sincronização inválida ou ausente." }, statusCode: StatusCodes.Status401Unauthorized);

    var status = await store.SaveServerGeminiApiKeyAsync(request.GeminiApiKey, ct);
    await TryFlushCloudMirrorAsync(httpContext, ct);
    return Results.Ok(status);
}).DisableAntiforgery();

app.MapPost("/api/ai/product-copy", async (
    GenerateProductCopyRequest request,
    GeminiProductAssistantService aiService,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.ProductName))
        return Results.BadRequest(new { error = "Informe o nome do produto." });

    var result = await aiService.GenerateProductCopyAsync(request, ct);
    return Results.Ok(result);
}).DisableAntiforgery().RequireRateLimiting("cloud-accounts");

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

static string ResolveRateLimitPartitionIp(HttpContext ctx)
{
    var remote = ctx.Connection.RemoteIpAddress?.ToString();
    if (!string.IsNullOrWhiteSpace(remote))
        return remote;

    var cfIp = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()?.Trim();
    if (!string.IsNullOrWhiteSpace(cfIp) && System.Net.IPAddress.TryParse(cfIp, out _))
        return cfIp;

    return "unknown";
}

static async Task<(bool Verified, string? VerifiedEmail)> ResolveVerifiedCloudOwnerEmailAsync(
    HttpContext ctx,
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct)
{
    // Segurança crítica (Itens 1 & A): ignora completamente X-YouLink-Owner-Email não autenticado e
    // SÓ considera "verificado" quem autenticou com CloudSessionToken E confirmou posse do e-mail por OTP (IsEmailVerified).
    var cloudToken = ctx.Request.Headers["X-YouLink-Cloud-Token"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(cloudToken))
        return (false, null);

    var validated = await accountRegistry.ValidateSessionTokenAsync(cloudToken, ct);
    if (!validated.Verified || !validated.IsEmailVerified || string.IsNullOrWhiteSpace(validated.Email))
        return (false, null);

    return (true, validated.Email);
}

static async Task<(bool Verified, string? Email, string? Username, string? StoreSlug, bool AuthorizedByPublishToken, bool IsEmailVerified)> AuthenticateMerchantSessionOrPublishAsync(
    string? bodyCloudSessionToken,
    string? bodyPublishToken,
    string? requestedStoreSlug,
    HttpContext ctx,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CancellationToken ct)
{
    var effectivePublishToken = !string.IsNullOrWhiteSpace(bodyPublishToken)
        ? bodyPublishToken
        : ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(effectivePublishToken) && !string.IsNullOrWhiteSpace(requestedStoreSlug))
    {
        var tokenOk = await gateway.StoreResolver.VerifyPublishTokenAsync(requestedStoreSlug, effectivePublishToken, ct);
        if (tokenOk)
        {
            var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(requestedStoreSlug, ct);
            var normalizedSlug = gateway.StoreResolver.NormalizeStoreSlug(requestedStoreSlug);
            return (true, creds?.OwnerEmail, null, normalizedSlug, true, creds?.IsOwnerEmailVerified == true);
        }
    }

    // Segurança crítica (Item 3): NUNCA aceita PasswordHash; exige CloudSessionToken ou PublishToken válido.
    var effectiveCloudToken = !string.IsNullOrWhiteSpace(bodyCloudSessionToken)
        ? bodyCloudSessionToken
        : ctx.Request.Headers["X-YouLink-Cloud-Token"].FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(effectiveCloudToken))
    {
        var sessionAuth = await accountRegistry.ValidateSessionTokenAsync(effectiveCloudToken, ct);
        if (sessionAuth.Verified)
            return (true, sessionAuth.Email, sessionAuth.Username, sessionAuth.StoreSlug, false, sessionAuth.IsEmailVerified);
    }

    return (false, null, null, null, false, false);
}

static async Task<(bool Authorized, string? AuthorizedStoreSlug, IResult? FailureResult)> AuthorizeMerchantStoreChatAccessAsync(
    MerchantChatAuthRequest? authRequest,
    HttpContext ctx,
    CloudAccountRegistryService accountRegistry,
    MultiTenantGatewayService gateway,
    CancellationToken ct,
    bool allowEmptyWhenUnpublished = false)
{
    if (authRequest is null)
        return (false, null, Results.Unauthorized());

    var auth = await AuthenticateMerchantSessionOrPublishAsync(
        authRequest.CloudSessionToken,
        authRequest.PublishToken,
        authRequest.StoreSlug,
        ctx,
        accountRegistry,
        gateway,
        ct);

    if (!auth.Verified)
        return (false, null, Results.Unauthorized());

    var candidateSlug = !string.IsNullOrWhiteSpace(authRequest.StoreSlug)
        ? authRequest.StoreSlug
        : auth.StoreSlug;

    if (string.IsNullOrWhiteSpace(candidateSlug))
    {
        return allowEmptyWhenUnpublished
            ? (false, null, Results.Ok(Array.Empty<ChatThreadSummaryDto>()))
            : (false, null, Results.BadRequest(new { error = "Informe o identificador (slug) da loja." }));
    }

    var normalizedSlug = gateway.StoreResolver.NormalizeStoreSlug(candidateSlug);

    // Se a loja ainda está com o nome/slug padrão não configurado ("minha-loja") ou ainda não foi publicada
    // no índice multi-tenant do servidor, na listagem retorna lista vazia (200 OK) sem erro 403 falso e sem vazar dados.
    if (allowEmptyWhenUnpublished)
    {
        if (string.Equals(normalizedSlug, "minha-loja", StringComparison.OrdinalIgnoreCase) ||
            !await gateway.StoreResolver.TenantExistsAsync(normalizedSlug, ct))
        {
            return (false, null, Results.Ok(Array.Empty<ChatThreadSummaryDto>()));
        }
    }

    var effectivePublishToken = !string.IsNullOrWhiteSpace(authRequest.PublishToken)
        ? authRequest.PublishToken
        : ctx.Request.Headers["X-YouLink-Publish-Token"].FirstOrDefault();

    // Segurança crítica (Item B): após o login, confere obrigatoriamente se a conta autenticada
    // é dona verificada daquele StoreSlug ou possui o PublishToken válido da loja.
    var ownsStore = await gateway.StoreResolver.AuthorizeTenantOrdersPullAsync(
        normalizedSlug,
        effectivePublishToken,
        hasValidSyncKey: false,
        authenticatedOwnerEmail: auth.Email,
        ct: ct,
        isOwnerEmailServerVerified: auth.IsEmailVerified);

    if (!ownsStore)
    {
        return (false, null, Results.Json(
            new { error = $"A loja '/loja/{normalizedSlug}' pertence a outra conta no servidor. Defina o nome exclusivo da sua loja em 'Minha Loja' e clique em Publicar Loja Online." },
            statusCode: StatusCodes.Status403Forbidden));
    }

    return (true, normalizedSlug, null);
}

static string GetPublicOrigin(HttpContext ctx)
{
    // Segurança crítica (Item 7): NUNCA confia em X-Forwarded-Host enviado pelo cliente (previne Host Header Injection).
    var configuredOrigin = Environment.GetEnvironmentVariable("YOULINK_PUBLIC_ORIGIN")
                           ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                           ?? Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
    if (!string.IsNullOrWhiteSpace(configuredOrigin))
    {
        var normalizedConfigured = WhatsAppLinkBuilder.NormalizePublicServerUrl(configuredOrigin);
        if (!string.IsNullOrWhiteSpace(normalizedConfigured))
            return normalizedConfigured;
    }

    var forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
    var scheme = string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase)
        ? "https"
        : (string.Equals(ctx.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http");

    var hostValue = ctx.Request.Host.Value?.Trim();
    if (string.IsNullOrWhiteSpace(hostValue))
        return string.Empty;

    foreach (var ch in hostValue)
    {
        var valid = char.IsLetterOrDigit(ch) || ch is '.' or '-' or ':' or '[' or ']';
        if (!valid)
            return string.Empty;
    }

    return $"{scheme}://{hostValue}";
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
    CloudAccountRegistryService accountRegistry,
    CancellationToken ct)
{
    var (rawBody, queryDataId, queryTopic, xSignature, xRequestId) = await ReadWebhookEnvelopeAsync(ctx, ct);
    var (dataId, topic) = MultiTenantGatewayService.ExtractPaymentNotification(queryDataId, queryTopic, rawBody);

    if (!mercadoPago.VerifyWebhookSignature(null, xSignature, xRequestId, queryDataId ?? dataId))
        return Results.Unauthorized();

    var res = await subscriptionService.HandleMercadoPagoWebhookDetailedAsync(topic, dataId, ct);
    if (res.Order is not null)
    {
        if (res.OrderApproved)
        {
            await accountRegistry.ApplyPlanAsync(
                res.Order.BuyerEmail,
                res.Order.BuyerUsername,
                res.Order.StoreName,
                res.Order.PlanTier,
                res.Order.DurationDays,
                extendExistingDuration: true,
                activateSite: true,
                ct: ct,
                isCourtesyVip: false,
                isRecurringSubscription: string.Equals(res.Order.PaymentMethod, PlatformSubscriptionOrder.MethodMercadoPagoSubscription, StringComparison.OrdinalIgnoreCase));
        }
        else if (res.IsBillingGraceOrFailure)
        {
            var graceNow = DateTimeOffset.UtcNow;
            var graceAnchor = res.Order.AppliedPlanExpiresAt.HasValue && res.Order.AppliedPlanExpiresAt.Value < graceNow
                ? res.Order.AppliedPlanExpiresAt.Value
                : graceNow;
            await accountRegistry.MarkRecurringPaymentFailureOrGraceAsync(
                res.Order.BuyerEmail,
                res.Order.BuyerUsername,
                res.Order.StoreName,
                graceAnchor,
                res.Message,
                ct);
        }

        await TryFlushCloudMirrorAsync(ctx, ct);
    }

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

        var isCourtesyVip = matchedTenantSettings is not null
            ? ResolveTenantIsCourtesyVip(matchedTenantSettings, acc.IsCourtesyVip)
            : acc.IsCourtesyVip;
        var graceEndsAt = matchedTenantSettings?.BillingGraceEndsAt ?? acc.BillingGraceEndsAt;
        var isBillingSuspended = matchedTenantSettings?.IsBillingSuspended ?? acc.IsBillingSuspended;
        var warningMsg = matchedTenantSettings?.LastPaymentFailureMessage ?? acc.BillingWarningMessage;
        var isRecurring = matchedTenantSettings?.IsRecurringSubscription ?? acc.IsRecurringSubscription;

        accounts.Add(new PlatformAccountSyncDto(
            UserId: Guid.Empty,
            EmployeeId: Guid.Empty,
            FullName: acc.FullName,
            Username: acc.Username,
            Email: acc.Email,
            RoleName: RoleNames.Admin,
            JobTitle: matchedTenantSettings?.StoreName ?? acc.StoreName,
            Phone: acc.Phone ?? matchedTenantSettings?.WhatsappNumber,
            IsActive: matchedTenantSettings?.IsSiteActive ?? acc.IsSiteActive,
            TwoFactorEnabled: false,
            CreatedAt: acc.CreatedAt,
            LastLoginAt: acc.UpdatedAt,
            StoreName: matchedTenantSettings?.StoreName ?? acc.StoreName,
            StoreSlug: matchedTenantSettings?.StoreSlug ?? acc.StoreSlug,
            OnlineBaseUrl: matchedUrl,
            PlanTier: matchedTier,
            PlanExpiresAt: matchedExpiry,
            IsCourtesyVip: isCourtesyVip,
            PlanRevokedAt: acc.PlanRevokedAt,
            IsRecurringSubscription: isRecurring,
            BillingGraceEndsAt: graceEndsAt,
            IsBillingSuspended: isBillingSuspended,
            BillingWarningMessage: warningMsg,
            LastClientCheckInAt: acc.LastClientCheckInAt,
            LastReportedClientPlanTier: acc.LastReportedClientPlanTier,
            LastClientAppVersion: acc.LastClientAppVersion));
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
            PlanExpiresAt: info.Settings.PlanExpiresAt,
            IsCourtesyVip: ResolveTenantIsCourtesyVip(info.Settings),
            PlanRevokedAt: null,
            IsRecurringSubscription: info.Settings.IsRecurringSubscription,
            BillingGraceEndsAt: info.Settings.BillingGraceEndsAt,
            IsBillingSuspended: info.Settings.IsBillingSuspended,
            BillingWarningMessage: info.Settings.LastPaymentFailureMessage));
    }

    // Enriquece linhas de repositório/tenant com telemetria de check-in do client e status mais recente do registro na nuvem.
    var cloudCheckInNow = DateTimeOffset.UtcNow;
    for (var i = 0; i < accounts.Count; i++)
    {
        var item = accounts[i];
        var matchedCloud = cloudAccounts.FirstOrDefault(ca =>
            (!string.IsNullOrWhiteSpace(item.Email) && !string.IsNullOrWhiteSpace(ca.Email) &&
             string.Equals(item.Email.Trim(), ca.Email.Trim(), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(item.Username) && !string.IsNullOrWhiteSpace(ca.Username) &&
             string.Equals(item.Username.Trim(), ca.Username.Trim(), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(item.StoreSlug) && !string.IsNullOrWhiteSpace(ca.StoreSlug) &&
             string.Equals(item.StoreSlug.Trim(), ca.StoreSlug.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (matchedCloud is null)
            continue;

        var caTier = CloudAccountRegistryService.ResolveEffectivePlan(matchedCloud.PlanTier, matchedCloud.PlanExpiresAt, cloudCheckInNow);
        var useCloudPlan = !string.Equals(caTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(item.PlanTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase);

        accounts[i] = item with
        {
            PlanTier = useCloudPlan ? caTier : item.PlanTier,
            PlanExpiresAt = useCloudPlan ? matchedCloud.PlanExpiresAt : item.PlanExpiresAt,
            IsCourtesyVip = useCloudPlan ? matchedCloud.IsCourtesyVip : item.IsCourtesyVip,
            PlanRevokedAt = item.PlanRevokedAt ?? matchedCloud.PlanRevokedAt,
            LastClientCheckInAt = item.LastClientCheckInAt ?? matchedCloud.LastClientCheckInAt,
            LastReportedClientPlanTier = item.LastReportedClientPlanTier ?? matchedCloud.LastReportedClientPlanTier,
            LastClientAppVersion = item.LastClientAppVersion ?? matchedCloud.LastClientAppVersion
        };
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
            PlanExpiresAt: settings.PlanExpiresAt,
            IsCourtesyVip: ResolveTenantIsCourtesyVip(settings),
            PlanRevokedAt: null,
            IsRecurringSubscription: settings.IsRecurringSubscription,
            BillingGraceEndsAt: settings.BillingGraceEndsAt,
            IsBillingSuspended: settings.IsBillingSuspended,
            BillingWarningMessage: settings.LastPaymentFailureMessage));
    }
}

static bool ResolveTenantIsCourtesyVip(StoreSettings settings, bool? cloudIsCourtesy = null) =>
    CloudPlatformPlanSyncHelper.ResolveTenantIsCourtesyVip(settings, cloudIsCourtesy);

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

static async Task ApplyGrantedCloudAccountVipToPublishedTenantAsync(
    MultiTenantGatewayService gateway,
    CloudAccountRegistryService accountRegistry,
    string? ownerEmail,
    string? publishedSlug,
    string? storeName,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(ownerEmail) || string.IsNullOrWhiteSpace(publishedSlug))
        return;

    var creds = await gateway.StoreResolver.GetTenantCredentialsAsync(publishedSlug, ct);
    if (creds is null ||
        !creds.IsOwnerEmailVerified ||
        string.IsNullOrWhiteSpace(creds.OwnerEmail) ||
        !string.Equals(creds.OwnerEmail.Trim(), ownerEmail.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var grantedMeta = await accountRegistry.GetPlanStatusWithAccountAsync(ownerEmail, null, publishedSlug, storeName, ct);
    if (grantedMeta is null)
        return;

    var granted = grantedMeta.Value.Status;
    var grantedAccount = grantedMeta.Value.Account;
    var now = DateTimeOffset.UtcNow;
    var grantedTier = CloudAccountRegistryService.ResolveEffectivePlan(granted.PlanTier, granted.PlanExpiresAt, now);
    if (string.Equals(grantedTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase))
        return;

    var tenantRepo = await gateway.ResolveReadRepositoryAsync(publishedSlug, ct);
    if (tenantRepo is null || ReferenceEquals(tenantRepo, gateway.StoreResolver.DefaultRepository))
        return;

    var settings = await tenantRepo.GetStoreSettingsAsync(ct);
    var currentTier = settings.GetEffectivePlanTier(now);
    var shouldApply =
        string.Equals(currentTier, StoreSettings.PlanTierBasic, StringComparison.OrdinalIgnoreCase) ||
        PlanCheckoutGuardHelper.GetTierRank(grantedTier) > PlanCheckoutGuardHelper.GetTierRank(currentTier) ||
        (grantedAccount.PlanUpdatedAt.HasValue &&
         grantedAccount.PlanUpdatedAt.Value > (settings.PlanActivatedAt ?? settings.UpdatedAt));

    if (!shouldApply)
        return;

    settings.PlanExpiresAt = granted.PlanExpiresAt;
    settings.PlanActivatedAt = now;
    settings.UpdatePlanAndDesign(grantedTier, designConfig: settings.DesignConfig);
    settings.ActivePlanBillingKind = granted.IsCourtesyVip
        ? PlanActivationCode.BillingKindGiftedUnpaid
        : PlanActivationCode.BillingKindPaid;
    settings.IsRecurringSubscription = granted.IsRecurringSubscription;
    if (granted.IsSiteActive)
        settings.ClearBillingGraceAndActivate(null, now);
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
                    httpContext.Response.Headers.CacheControl = "public, max-age=86400, stale-while-revalidate=604800";
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
