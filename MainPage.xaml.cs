using System.Net.Http.Json;
using ClothingStore.Shared.Helpers;

namespace appmaui.blazor;

public partial class MainPage : ContentPage
{
    public const string AndroidCloudPanelModePrefKey = "youlink_android_cloud_panel_mode_v1";
    private const string PreferredCloudPanelUrlPrefKey = "youlink_preferred_cloud_panel_url_v1";

    private static readonly HttpClient CloudConfigHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private bool _cloudModeActive;
    private bool _cloudPageLoaded;
    private CancellationTokenSource? _loadTimeoutCts;

    public MainPage()
    {
        InitializeComponent();

        if (OperatingSystem.IsAndroid() && Preferences.Default.Get(AndroidCloudPanelModePrefKey, true))
        {
            ConfigureCloudWebViewHandler();
            StartCloudPanelMode();
        }
    }

    private void ConfigureCloudWebViewHandler()
    {
#if ANDROID
        cloudPanelWebView.HandlerChanged += (_, _) =>
        {
            if (cloudPanelWebView.Handler?.PlatformView is Android.Webkit.WebView androidWebView)
            {
                var settings = androidWebView.Settings;
                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.DatabaseEnabled = true;
                settings.AllowFileAccess = true;
                settings.AllowContentAccess = true;
                settings.LoadsImagesAutomatically = true;
                settings.MixedContentMode = Android.Webkit.MixedContentHandling.CompatibilityMode;
            }
        };
#endif
    }

    private void StartCloudPanelMode()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            FallbackToLocalMode("Sem internet — modo local offline ativo");
            return;
        }

        _cloudModeActive = true;
        _cloudPageLoaded = false;

        offlineFallbackBar.IsVisible = false;
        blazorWebView.IsVisible = false;
        cloudPanelWebView.IsVisible = true;
        cloudLoadingOverlay.IsVisible = true;

        var targetUrl = ResolveCloudPanelUrl();
        cloudPanelWebView.Source = new UrlWebViewSource { Url = targetUrl };

        StartLoadWatchdog();
        _ = RefreshPreferredCloudPanelUrlInBackgroundAsync(targetUrl);
    }

    private static string ResolveCloudPanelUrl()
    {
        var cached = Preferences.Default.Get(PreferredCloudPanelUrlPrefKey, string.Empty)?.Trim();
        var baseUrl = !string.IsNullOrWhiteSpace(cached) && Uri.TryCreate(cached, UriKind.Absolute, out var parsed) &&
                      (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            ? cached
            : AppUpdateCatalog.DefaultCloudPanelUrl;

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}app=android&v={Uri.EscapeDataString(AppUpdateCatalog.CurrentAppVersion)}";
    }

    private void StartLoadWatchdog()
    {
        _loadTimeoutCts?.Cancel();
        _loadTimeoutCts?.Dispose();
        _loadTimeoutCts = new CancellationTokenSource();
        var token = _loadTimeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(18), token);
                if (!token.IsCancellationRequested && _cloudModeActive && !_cloudPageLoaded)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_cloudModeActive && !_cloudPageLoaded)
                        {
                            FallbackToLocalMode("Servidor demorou para responder — modo local ativo");
                        }
                    });
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, token);
    }

    private async Task RefreshPreferredCloudPanelUrlInBackgroundAsync(string currentlyLoadedUrl)
    {
        try
        {
            var config = await CloudConfigHttpClient.GetFromJsonAsync<CloudPwaConfigDto>(AppUpdateCatalog.DefaultCloudPwaConfigUrl);
            var remotePanelUrl = config?.PanelUrl?.Trim();
            if (string.IsNullOrWhiteSpace(remotePanelUrl) ||
                !Uri.TryCreate(remotePanelUrl, UriKind.Absolute, out var remoteUri) ||
                remoteUri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }

            var previous = Preferences.Default.Get(PreferredCloudPanelUrlPrefKey, string.Empty)?.Trim();
            if (!string.Equals(previous, remotePanelUrl, StringComparison.OrdinalIgnoreCase))
            {
                Preferences.Default.Set(PreferredCloudPanelUrlPrefKey, remotePanelUrl);

                // Se acabou de ativar o Cloudflare Edge e ainda estava no Render, migra suavemente
                if (config?.EdgeEnabled == true &&
                    !currentlyLoadedUrl.StartsWith(remotePanelUrl, StringComparison.OrdinalIgnoreCase))
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_cloudModeActive && !_cloudPageLoaded)
                        {
                            cloudPanelWebView.Source = new UrlWebViewSource { Url = ResolveCloudPanelUrl() };
                        }
                    });
                }
            }
        }
        catch
        {
            // Mantém silenciosamente a URL atual
        }
    }

    private void OnCloudWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Url))
            return;

        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            return;

        var host = uri.Host.ToLowerInvariant();
        var scheme = uri.Scheme.ToLowerInvariant();

        var isExternalAppScheme = scheme is "tel" or "mailto" or "whatsapp" or "intent";
        var isWhatsAppWeb = host.Contains("wa.me", StringComparison.Ordinal) ||
                            host.Contains("whatsapp.com", StringComparison.Ordinal);
        var isPublicStorePreview = uri.AbsolutePath.StartsWith("/loja/", StringComparison.OrdinalIgnoreCase);

        if (isExternalAppScheme || isWhatsAppWeb || isPublicStorePreview)
        {
            e.Cancel = true;
            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    await Launcher.Default.OpenAsync(uri);
                }
                catch
                {
                }
            });
        }
    }

    private void OnCloudWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _loadTimeoutCts?.Cancel();

        if (!_cloudModeActive)
            return;

        if (e.Result == WebNavigationResult.Success)
        {
            _cloudPageLoaded = true;
            cloudLoadingOverlay.IsVisible = false;
            return;
        }

        FallbackToLocalMode("Sem conexão com a nuvem — modo local ativo");
    }

    private void FallbackToLocalMode(string statusMessage)
    {
        _loadTimeoutCts?.Cancel();
        _cloudModeActive = false;
        _cloudPageLoaded = false;

        cloudLoadingOverlay.IsVisible = false;
        cloudPanelWebView.IsVisible = false;
        blazorWebView.IsVisible = true;
        offlineStatusLabel.Text = statusMessage;
        offlineFallbackBar.IsVisible = OperatingSystem.IsAndroid();
    }

    private void OnSwitchToLocalClicked(object? sender, EventArgs e)
    {
        FallbackToLocalMode("Modo local offline ativo");
    }

    private void OnRetryCloudClicked(object? sender, EventArgs e)
    {
        StartCloudPanelMode();
    }

    protected override bool OnBackButtonPressed()
    {
        if (_cloudModeActive && cloudPanelWebView.IsVisible && cloudPanelWebView.CanGoBack)
        {
            cloudPanelWebView.GoBack();
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private sealed class CloudPwaConfigDto
    {
        public string? PanelUrl { get; set; }
        public string? ApiBase { get; set; }
        public string? Version { get; set; }
        public bool EdgeEnabled { get; set; }
    }
}
