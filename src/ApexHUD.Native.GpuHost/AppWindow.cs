using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ApexHUD.Native.GpuHost
{
    /// <summary>
    /// 2026-11-05 (Angelo: "when people use this it needs to be like a program like engineer app"):
    /// Grandstand opens as a normal desktop app - its own window with the control page inside (Edge WebView2,
    /// the same engine the Race Engineer app uses), a taskbar button and an icon, instead of a browser tab.
    /// The window runs on its own thread with its own WPF dispatcher so it never touches the render loop.
    ///   - closing the window quits Grandstand (graphics and all), like any program
    ///   - minimising keeps the broadcast running
    ///   - starting Grandstand a second time brings this window back instead of starting a second copy
    ///   - no WebView2 on the PC (rare on Windows 10/11): the control page opens in the default browser instead
    ///   - --no-window: start without the window (the page is still at http://127.0.0.1:8790)
    /// </summary>
    internal static class AppWindow
    {
        public static volatile bool QuitRequested;
        static Window? window;
        static CoreWebView2Environment? webEnv;
        static Action<string>? logFn;
        static bool IsOurs(string url) => url.StartsWith(ControlServer.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>2026-11-06 (Angelo: "the Grandstand booth should not open Chrome - just a window in Grandstand"): a page of
        /// Grandstand's own (booth, previews...) opens in its own Grandstand window; only outside links go to the browser.</summary>
        static void OpenChild(string url)
        {
            try
            {
                var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x05, 0x07, 0x0E) };
                var work = SystemParameters.WorkArea;
                var cw = new Window
                {
                    Title = "Grandstand",
                    Width = Math.Min(1280, work.Width * 0.85), Height = Math.Min(860, work.Height * 0.85), MinWidth = 640, MinHeight = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0E)),
                    Content = web,
                };
                try
                {
                    string ico = Path.Combine(AppContext.BaseDirectory, "grandstand.ico");
                    if (File.Exists(ico)) cw.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico));
                }
                catch { }
                ApplyDarkTitleBar(cw);
                cw.Loaded += async (_, _) =>
                {
                    try
                    {
                        await web.EnsureCoreWebView2Async(webEnv);
                        var core = web.CoreWebView2;
                        core.Settings.IsStatusBarEnabled = false;
                        core.Settings.AreDevToolsEnabled = false;
                        core.NewWindowRequested += (_, a) => { a.Handled = true; if (IsOurs(a.Uri)) OpenChild(a.Uri); else OpenUrl(a.Uri); };
                        core.NavigationStarting += (_, a) => { if (!IsOurs(a.Uri)) { a.Cancel = true; OpenUrl(a.Uri); } };
                        core.DocumentTitleChanged += (_, _) => { try { cw.Title = core.DocumentTitle is { Length: > 0 } t ? t : "Grandstand"; } catch { } };
                        core.Navigate(url);
                    }
                    catch (Exception ex) { logFn?.Invoke("app window: child page failed: " + ex.Message); }
                };
                cw.Show();
                logFn?.Invoke("app window: opened " + url + " in a Grandstand window");
            }
            catch (Exception ex) { logFn?.Invoke("app window: child window failed: " + ex.Message); }
        }
        static Dispatcher? dispatcher;
        static Mutex? single;
        const string MutexName = "Local\\Grandstand.App";
        const string ShowEventName = "Local\\Grandstand.ShowWindow";

        /// <summary>False when another Grandstand is already running - it has been asked to show its window; exit.</summary>
        public static bool ClaimSingleInstance()
        {
            try
            {
                single = new Mutex(true, MutexName, out bool created);
                if (created) return true;
                // already running: wait in case it is closing (the launcher restarts it); otherwise ask it to show its
                // window - and if it still goes away soon after (it WAS closing, just slowly), take over instead of
                // leaving nothing running
                try { if (single.WaitOne(3000)) return true; } catch (AbandonedMutexException) { return true; }
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
                try { if (single.WaitOne(8000)) return true; } catch (AbandonedMutexException) { return true; }
                return false;
            }
            catch { return true; }
        }

        public static void Start(Action<string> log)
        {
            var t = new Thread(() => Run(log)) { Name = "Grandstand-AppWindow", IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();

            // a second launch signals this event: bring the window forward
            new Thread(() =>
            {
                try
                {
                    using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                    while (!QuitRequested) if (ev.WaitOne(1000)) ShowWindow();
                }
                catch { }
            }) { IsBackground = true, Name = "Grandstand-ShowWatch" }.Start();
        }

        public static void ShowWindow()
        {
            var d = dispatcher; var w = window;
            if (d == null || w == null) { OpenInBrowser(); return; }
            d.BeginInvoke(() =>
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Show(); w.Activate(); w.Topmost = true; w.Topmost = false; w.Focus();
            });
        }

        /// <summary>Grandstand is quitting another way (Ctrl+Shift+Q): take the window down with it.</summary>
        public static void Close()
        {
            QuitRequested = true;
            try { dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
        }

        static void OpenInBrowser()
        {
            try { Process.Start(new ProcessStartInfo(ControlServer.Url) { UseShellExecute = true }); } catch { }
        }

        static void Run(Action<string> log)
        {
            string? version = null;
            try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrEmpty(version))
            {
                log("app window: WebView2 not installed - control page opened in the browser instead");
                OpenInBrowser();
                return;
            }

            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var bg = Color.FromRgb(0x05, 0x07, 0x0E);
                var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x05, 0x07, 0x0E) };
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "WebView2");

                var work = SystemParameters.WorkArea;
                double wd = Math.Min(1500, work.Width * 0.9), ht = Math.Min(940, work.Height * 0.9);
                var w = new Window
                {
                    Title = "Grandstand",
                    Width = wd, Height = ht, MinWidth = 900, MinHeight = 600,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = new SolidColorBrush(bg),
                    Content = web,
                };
                try
                {
                    string ico = Path.Combine(AppContext.BaseDirectory, "grandstand.ico");
                    if (File.Exists(ico)) w.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico));
                }
                catch { }
                ApplyDarkTitleBar(w);

                w.Closed += (_, _) =>
                {
                    QuitRequested = true;
                    dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                web.CoreWebView2InitializationCompleted += (_, e) =>
                {
                    if (!e.IsSuccess) { log("app window: WebView2 failed (" + e.InitializationException?.Message + ") - browser instead"); OpenInBrowser(); return; }
                    var core = web.CoreWebView2;
                    core.Settings.IsStatusBarEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.IsZoomControlEnabled = true;
                    // links to anything other than the control page (GitHub, donate, help) open in the normal browser
                    core.NewWindowRequested += (_, a) => { a.Handled = true; if (IsOurs(a.Uri)) OpenChild(a.Uri); else OpenUrl(a.Uri); };
                    core.NavigationStarting += (_, a) =>
                    {
                        if (!a.Uri.StartsWith(ControlServer.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenUrl(a.Uri); }
                    };
                    core.DocumentTitleChanged += (_, _) => { };
                };
                window = w;
                w.Loaded += async (_, _) =>
                {
                    try
                    {
                        Directory.CreateDirectory(data);
                        var env = await CoreWebView2Environment.CreateAsync(null, data);
                        webEnv = env; logFn = log;
                        await web.EnsureCoreWebView2Async(env);
                        web.CoreWebView2.NavigationCompleted += (_, n) =>
                        {
                            if (!n.IsSuccess) log("app window: page failed to load (" + n.WebErrorStatus + ")");
                        };
                        web.CoreWebView2.Navigate(ControlServer.Url);
                        log("app window: page loading");
                    }
                    catch (Exception ex)
                    {
                        log("app window: WebView2 start failed: " + ex.GetType().Name + ": " + ex.Message + " - browser instead");
                        OpenInBrowser();
                    }
                };
                w.Show();
                log("app window: open (WebView2 " + version + ")");
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                log("app window failed: " + ex.GetType().Name + ": " + ex.Message + " - control page opened in the browser instead");
                window = null;
                OpenInBrowser();
            }
        }

        static void OpenUrl(string url)
        {
            try { if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static void ApplyDarkTitleBar(Window w)
        {
            w.SourceInitialized += (_, _) =>
            {
                try
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    int on = 1;
                    if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
                    // Windows 11: the title bar in the broadcast navy, ivory title, a thin gold window edge (0x00BBGGRR)
                    int caption = 0x00120B07, text = 0x00ECF4F7, border = 0x00527AA8;
                    DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));
                    DwmSetWindowAttribute(h, 36, ref text, sizeof(int));
                    DwmSetWindowAttribute(h, 34, ref border, sizeof(int));
                }
                catch { }
            };
        }
    }
}
