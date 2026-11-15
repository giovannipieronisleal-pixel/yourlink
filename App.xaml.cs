using appmaui.blazor.Services;

namespace appmaui.blazor
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage()) { Title = "YouLink" };
            window.Activated += (_, _) => TriggerUpdateCheck(window);
            window.Resumed += (_, _) => TriggerUpdateCheck(window);
            window.Destroying += (_, _) => OnMainWindowDestroying();
#if WINDOWS
            window.HandlerChanged += (_, _) =>
            {
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUiWindow)
                {
                    winUiWindow.Closed -= OnNativeWindowsWindowClosed;
                    winUiWindow.Closed += OnNativeWindowsWindowClosed;
                }
            };
#endif
            return window;
        }

#if WINDOWS
        private static void OnNativeWindowsWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            appmaui.blazor.WinUI.App.HandleMainWindowClosed();
        }
#endif

        private static void OnMainWindowDestroying()
        {
            LocalWebServerLauncher.Stop();
#if WINDOWS
            appmaui.blazor.WinUI.App.HandleMainWindowClosed();
#endif
        }

        private static void TriggerUpdateCheck(Window window)
        {
            try
            {
                var services = window.Handler?.MauiContext?.Services
                    ?? Current?.Handler?.MauiContext?.Services
                    ?? IPlatformApplication.Current?.Services;
                var updateService = services?.GetService<AppUpdateService>();
                if (updateService is not null)
                {
                    _ = updateService.CheckForUpdatesOnResumeAsync();
                }
            }
            catch
            {
            }
        }
    }
}
