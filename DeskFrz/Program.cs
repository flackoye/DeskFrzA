using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskFrz;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var data = Option("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskFrz");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        try
        {
            using var dpi = new Native.DpiScope();
            if (args.Contains("--selftest"))
            {
                var result = JsonSerializer.Serialize(SelfTest.Run(data), json);
                if (Option("--output") is { } output) File.WriteAllText(output, result); else Console.WriteLine(result);
                return;
            }
            if (args.Contains("--validate-screen-backup"))
            {
                var screens = Native.Displays.Read();
                var backup = Native.Displays.CaptureConfiguration(screens); backup.Validate(screens);
                var result = JsonSerializer.Serialize(new { validated = true, screens = screens.Count }, json);
                if (Option("--output") is { } output) File.WriteAllText(output, result); else Console.WriteLine(result);
                return;
            }
            var service = new DesktopService(new Store(data));
            if (args.Contains("--probe"))
            {
                var result = JsonSerializer.Serialize(service.Capture(), json);
                if (Option("--output") is { } output) File.WriteAllText(output, result); else Console.WriteLine(result);
                return;
            }
            using var instance = new Mutex(true, "Local\\DeskFrz-" + Fingerprint.Of(Path.GetFullPath(data)), out var first);
            if (!first) { SingleInstance.ActivateExisting(data); return; }
            var application = new Application { ShutdownMode = args.Contains("--serve") ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose };
            using var updates = new UpdateService(data, !args.Contains("--serve"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
                Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:" + (Option("--port") ?? "0"));
            var host = builder.Build();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string baseUrl = "";
            bool nativeReady = false;
            Window? mainWindow = null;
            WebView2? desktopView = null;
            host.Use(async (context, next) =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                context.Response.Headers["Cache-Control"] = "no-store";
                var origin = context.Request.Headers.Origin.ToString();
                if (context.Request.Host.ToString() != new Uri(baseUrl).Authority
                    || (!string.IsNullOrEmpty(origin) && origin != baseUrl)
                    || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Method != "GET" && context.Request.Headers["X-DeskFrz-Token"] != token)
                { context.Response.StatusCode = 403; return; }
                try { await next(context); }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 409;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });
            host.UseDefaultFiles(); host.UseStaticFiles();
            async Task<IResult> OnDesktop(Func<object> action) => Results.Json(await application.Dispatcher.InvokeAsync(action));
            host.MapGet("/api/session", () => Results.Json(new { token, version = updates.Status.CurrentVersion }));
            host.MapGet("/api/health", () => Results.Json(new { mode = args.Contains("--serve") ? "browser" : "native", nativeReady }));
            host.MapPost("/api/activate", () => OnDesktop(() =>
            {
                if (mainWindow == null) throw new InvalidOperationException("当前实例为开发服务，没有应用窗口。");
                mainWindow.Show();
                if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                return new { activated = true };
            }));
            host.MapGet("/api/state", () => OnDesktop(service.State));
            host.MapPost("/api/save", (SaveRequest request) => OnDesktop(() => service.Save(request.Single)));
            host.MapPost("/api/single-default", (DefaultSingleRequest request) => OnDesktop(() => service.DefaultSingle(request.Id)));
            host.MapPost("/api/single-retry", () => OnDesktop(service.RetrySingleRecovery));
            host.MapPost("/api/rename", (RenameRequest request) => OnDesktop(() => service.Rename(request.Id, request.Name)));
            host.MapPost("/api/delete", (IdRequest request) => OnDesktop(() => service.Delete(request.Id)));
            host.MapPost("/api/preview", (PreviewRequest request) => OnDesktop(() => service.PreviewWithState(request.Id, request.TargetId, request.AllowResize)));
            host.MapPost("/api/restore", (RestoreRequest request) => OnDesktop(() => service.Restore(request.Id, request.CurrentKey, request.DesktopKey, false, request.TargetId, request.AllowResize, request.AllowFreePlacement)));
            host.MapPost("/api/undo-preview", () => OnDesktop(service.UndoPreview));
            host.MapPost("/api/undo", (RestoreRequest request) => OnDesktop(() => service.Restore(request.Id, request.CurrentKey, request.DesktopKey, true)));
            host.MapPost("/api/undo-screen-switch", (RestoreRequest request) => OnDesktop(() => service.UndoScreenSwitch(request.CurrentKey, request.DesktopKey)));
            host.MapPost("/api/auto-arrange-off", () => OnDesktop(service.DisableAutoArrange));
            host.MapPost("/api/settings", (SettingsRequest request) => OnDesktop(() => service.Settings(request.PanelGap, request.CheckUpdates)));
            host.MapGet("/api/update/status", () => Results.Json(updates.Status));
            host.MapPost("/api/update/check", async (CancellationToken cancellation) => Results.Json(await updates.CheckAsync(cancellation)));
            host.MapPost("/api/update/download", async (UpdateRequest request, HttpContext context) => Results.Json(await updates.DownloadAsync(request.Version, context.RequestAborted)));
            host.MapPost("/api/update/install", async (UpdateRequest request, HttpContext context) =>
            {
                var path = await updates.PrepareInstallAsync(request.Version, context.RequestAborted);
                updates.LaunchInstaller(path);
                context.Response.OnCompleted(async () =>
                {
                    await Task.Delay(500);
                    await application.Dispatcher.InvokeAsync(() => application.Shutdown());
                });
                return Results.Json(new { installing = true });
            });
            // Keep the last valid pre-switch desktop even while the application is minimized.
            var screenWatcher = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            void ObserveDesktop() {
                try { service.Observe(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                { System.Diagnostics.Trace.TraceWarning("Desktop observation deferred: {0}", ex.Message); }
            }
            EventHandler displayChanged = (_, _) => application.Dispatcher.BeginInvoke((Action)ObserveDesktop);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanging += displayChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayChanged;
            service.ScreenSwitched += () => {
                // Queue the notification after capture/state finishes on the STA dispatcher.
                application.Dispatcher.BeginInvoke((Action)(async () => {
                    if (mainWindow is null) return;
                    mainWindow.Show();
                    if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                    mainWindow.Activate();
                    if (desktopView?.CoreWebView2 is { } core)
                        try { await core.ExecuteScriptAsync("window.dispatchEvent(new Event('deskfrza-screen-switch'))"); }
                        catch (InvalidOperationException ex) { System.Diagnostics.Trace.TraceWarning("Screen notification deferred: {0}", ex.Message); }
                }));
            };
            screenWatcher.Tick += (_, _) => ObserveDesktop();
            ObserveDesktop();
            screenWatcher.Start();
            host.StartAsync().GetAwaiter().GetResult();
            baseUrl = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "session.json"), JsonSerializer.Serialize(new { url = baseUrl, processId = Environment.ProcessId }, json));
            // Async host disposal must not capture the exiting WPF dispatcher.
            try
            {
                if (args.Contains("--serve")) { Console.WriteLine(baseUrl); application.Run(); }
                else
                {
                    var webview = new WebView2();
                    desktopView = webview;
                    var window = new Window { Title = "DeskFrzA", Width = 1380, Height = 920,
                        MinWidth = 920, MinHeight = 640, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#F8F6EF")!, Content = webview };
                    mainWindow = window;
                    window.Closed += (_, _) => webview.Dispose();
                    window.Loaded += async (_, _) =>
                    {
                        try
                        {
                            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "webview"));
                            await webview.EnsureCoreWebView2Async(environment);
                            webview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                            webview.CoreWebView2.Settings.AreDevToolsEnabled = args.Contains("--dev");
                            webview.CoreWebView2.Settings.IsStatusBarEnabled = false;
                            void OpenExternal(string address)
                            {
                                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                                    || uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443 || uri.UserInfo.Length != 0) return;
                                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                                catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开 GitHub"); }
                            }
                            webview.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
                            webview.CoreWebView2.NavigationCompleted += (_, e) => nativeReady = e.IsSuccess;
                            webview.CoreWebView2.NavigationStarting += (_, e) =>
                            { if (!e.Uri.StartsWith(baseUrl + "/", StringComparison.Ordinal)) { e.Cancel = true; OpenExternal(e.Uri); } };
                            webview.Source = new Uri(baseUrl);
                            if (args.Contains("--lifecycle-test"))
                                await SingleInstance.TestLifecycle(window, data, Option("--output")!);
                        }
                        catch (Exception ex) { MessageBox.Show(ex.Message, "DeskFrzA 启动失败"); window.Close(); }
                    };
                    application.Run(window);
                }
            }
            finally
            {
                screenWatcher.Stop();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanging -= displayChanged;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayChanged;
                Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await host.StopAsync(timeout.Token); }
                    finally { await host.DisposeAsync(); }
                }).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            if (Option("--output") is { } output) File.WriteAllText(output, JsonSerializer.Serialize(new { error = ex.ToString() }, json));
            else if (args.Any(a => a.StartsWith("--"))) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "DeskFrzA");
            Environment.ExitCode = 1;
        }
    }
    public record IdRequest(string Id);
    public record SaveRequest(bool Single = false);
    public record DefaultSingleRequest(string? Id = null);
    public record RenameRequest(string Id, string Name);
    public record PreviewRequest(string Id, string? TargetId = null, bool AllowResize = false);
    public record RestoreRequest(string Id, string CurrentKey, string DesktopKey, string? TargetId = null, bool AllowResize = false, bool AllowFreePlacement = false);
    public record SettingsRequest(int PanelGap, bool? CheckUpdates = null);
    public record UpdateRequest(string Version);
}
