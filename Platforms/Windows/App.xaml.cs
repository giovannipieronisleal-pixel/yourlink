using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace appmaui.blazor.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// Enforces strict single-instance window behavior on Windows so double-clicking YouLink
    /// brings the active window to the foreground instead of spawning a second window.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        private const string SingleInstanceMutexName = @"Local\YouLink_AppWindow_Mutex_v1";
        private const string ActivateWindowEventName = @"Local\YouLink_ActivateWindow_Event_v1";
        private const int SwShow = 5;
        private const int SwRestore = 9;

        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _activateWindowEvent;
        private static int _hasAcquiredMutex;
        private static int _isShuttingDown;

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += (_, _) => PerformCleanProcessCleanup();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (!TryAcquireSingleInstance())
            {
                ActivateExistingYouLinkWindow();
                Environment.Exit(0);
                return;
            }

            StartActivationSignalListener();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => PerformCleanProcessCleanup();

            base.OnLaunched(args);
        }

        public static void HandleMainWindowClosed()
        {
            if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
                return;

            PerformCleanProcessCleanup();

            try
            {
                Microsoft.Maui.Controls.Application.Current?.Quit();
            }
            catch
            {
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
                catch
                {
                }
                Environment.Exit(0);
            });
        }

        private static void PerformCleanProcessCleanup()
        {
            try
            {
                appmaui.blazor.Services.LocalWebServerLauncher.Stop();
            }
            catch
            {
            }

            ReleaseSingleInstanceHandles();
        }

        private static bool TryAcquireSingleInstance()
        {
            if (_hasAcquiredMutex == 1)
                return true;

            _singleInstanceMutex = new Mutex(initiallyOwned: false, name: SingleInstanceMutexName);

            bool acquired = TryWaitMutex(_singleInstanceMutex, 0);

            // Se outro processo já segura o mutex, aguarda brevemente caso esteja em boot inicial ou encerrando;
            // se continuar preso sem janela visível após o prazo, encerra os órfãos e retoma o mutex.
            if (!acquired && !HasExistingYouLinkWindow())
            {
                var hasFreshSibling = HasFreshSiblingAppProcess(maxAgeMs: 6000);
                var maxPollSteps = hasFreshSibling ? 20 : 4;

                for (var step = 0; step < maxPollSteps && !acquired; step++)
                {
                    if (HasExistingYouLinkWindow())
                        break;

                    acquired = TryWaitMutex(_singleInstanceMutex, 200);
                }

                if (!acquired && !HasExistingYouLinkWindow())
                {
                    TerminateOrphanWindowlessSiblings();
                    acquired = TryWaitMutex(_singleInstanceMutex, 1500);
                }
            }

            if (acquired)
            {
                Interlocked.Exchange(ref _hasAcquiredMutex, 1);
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        private static bool TryWaitMutex(Mutex mutex, int timeoutMs)
        {
            try
            {
                return mutex.WaitOne(timeoutMs, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static bool HasFreshSiblingAppProcess(double maxAgeMs)
        {
            var currentPid = Environment.ProcessId;
            var nowUtc = DateTime.UtcNow;

            foreach (var proc in Process.GetProcessesByName("appmaui.blazor"))
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id == currentPid || proc.HasExited)
                            continue;

                        var ageMs = (nowUtc - proc.StartTime.ToUniversalTime()).TotalMilliseconds;
                        if (ageMs < maxAgeMs)
                            return true;
                    }
                    catch
                    {
                    }
                }
            }

            return false;
        }

        private static void TerminateOrphanWindowlessSiblings()
        {
            var currentPid = Environment.ProcessId;

            foreach (var proc in Process.GetProcessesByName("appmaui.blazor"))
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id == currentPid || proc.HasExited)
                            continue;

                        if (FindExistingYouLinkWindowForPid((uint)proc.Id) != IntPtr.Zero)
                            continue;

                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void StartActivationSignalListener()
        {
            try
            {
                _activateWindowEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: ActivateWindowEventName);

                var listenerThread = new Thread(() =>
                {
                    while (_activateWindowEvent is not null)
                    {
                        try
                        {
                            if (!_activateWindowEvent.WaitOne())
                                break;

                            BringOwnWindowToFront();
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch
                        {
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "YouLinkSingleInstanceActivator"
                };

                listenerThread.Start();
            }
            catch
            {
            }
        }

        private static void BringOwnWindowToFront()
        {
            try
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
                        if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUiWindow)
                        {
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUiWindow);
                            if (hwnd != IntPtr.Zero)
                            {
                                ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
                                SetForegroundWindow(hwnd);
                            }

                            winUiWindow.Activate();
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }

        private static bool HasExistingYouLinkWindow() =>
            FindExistingYouLinkWindow(out _, out _) != IntPtr.Zero;

        private static void ActivateExistingYouLinkWindow()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateWindowEventName, out var ev))
                {
                    using (ev)
                    {
                        ev.Set();
                    }
                }
            }
            catch
            {
            }

            var hwnd = FindExistingYouLinkWindow(out var targetPid, out _);
            if (hwnd == IntPtr.Zero)
                return;

            if (targetPid != 0)
            {
                AllowSetForegroundWindow(targetPid);
            }

            ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
            SetForegroundWindow(hwnd);
        }

        private static bool IsIgnoredOverlayOrSystemClass(string? className) =>
            string.Equals(className, "UAC_InputIndicatorOverlayWnd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "UAC Input Indicator", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "IME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "CicLoaderWndClass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "Touch Tooltip Window", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "WEBVIEW2_TEMP_PARENT", StringComparison.OrdinalIgnoreCase);

        private static IntPtr FindExistingYouLinkWindowForPid(uint requiredPid) =>
            FindExistingYouLinkWindowCore(requiredPid, out _, out _);

        private static IntPtr FindExistingYouLinkWindow(out uint targetPid, out string? foundTitle) =>
            FindExistingYouLinkWindowCore(null, out targetPid, out foundTitle);

        private static IntPtr FindExistingYouLinkWindowCore(uint? requiredPid, out uint targetPid, out string? foundTitle)
        {
            uint currentPid = (uint)Environment.ProcessId;
            IntPtr matchedHwnd = IntPtr.Zero;
            uint matchedPid = 0;
            string? matchedTitle = null;
            IntPtr fallbackHwnd = IntPtr.Zero;
            uint fallbackPid = 0;
            string? fallbackTitle = null;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindow(hwnd))
                    return true;

                if (!IsWindowVisible(hwnd) && !IsIconic(hwnd))
                    return true;

                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                    return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == currentPid)
                    return true;

                if (requiredPid.HasValue && pid != requiredPid.Value)
                    return true;

                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    var procName = proc.ProcessName;
                    if (!string.Equals(procName, "appmaui.blazor", StringComparison.OrdinalIgnoreCase))
                        return true;

                    var clsSb = new StringBuilder(128);
                    GetClassName(hwnd, clsSb, clsSb.Capacity);
                    var className = clsSb.ToString();
                    if (IsIgnoredOverlayOrSystemClass(className))
                        return true;

                    if (string.Equals(className, "WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedHwnd = hwnd;
                        matchedPid = pid;
                        matchedTitle = proc.MainWindowTitle;
                        return false;
                    }

                    if (fallbackHwnd == IntPtr.Zero && GetWindowTextLength(hwnd) > 0)
                    {
                        fallbackHwnd = hwnd;
                        fallbackPid = pid;
                        fallbackTitle = proc.MainWindowTitle;
                    }

                    return true;
                }
                catch
                {
                    return true;
                }
            }, IntPtr.Zero);

            if (matchedHwnd != IntPtr.Zero)
            {
                targetPid = matchedPid;
                foundTitle = matchedTitle;
                return matchedHwnd;
            }

            targetPid = fallbackPid;
            foundTitle = fallbackTitle;
            return fallbackHwnd;
        }

        private static void ReleaseSingleInstanceHandles()
        {
            try
            {
                if (Interlocked.Exchange(ref _hasAcquiredMutex, 0) == 1)
                {
                    _singleInstanceMutex?.ReleaseMutex();
                }
            }
            catch
            {
            }

            try
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            catch
            {
            }

            try
            {
                _activateWindowEvent?.Dispose();
                _activateWindowEvent = null;
            }
            catch
            {
            }
        }

        private const uint GwOwner = 4;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
    }
}
