using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DeskFrz;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A stale Windows notification must never start the default data library.
        // Notification clicks are handled only by the live resident COM activator.
        if (args.Contains("-ToastActivated")) return;
        string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var data = Option("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskFrz");
        bool uiSelfTest = args.Contains("--ui-selftest");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        try
        {
            using var dpi = new Native.DpiScope();
            if ((args.Contains("--memory-test") || args.Contains("--mode-test")) && (!uiSelfTest || Option("--output") is null))
                throw new InvalidOperationException("界面模式/内存自检必须使用 --ui-selftest 和 --output，并指定全新空数据目录。");
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
            if (uiSelfTest && Option("--data") is null)
                throw new InvalidOperationException("界面自检必须使用 --data 指定一个新的空目录。");
            var store = new Store(data);
            var service = uiSelfTest ? new DesktopService(store, RestoreTests.CreateUiFixture(store,
                args.Contains("--memory-test") || args.Contains("--lifecycle-test"))) : new DesktopService(store);
            if (args.Contains("--probe"))
            {
                var result = JsonSerializer.Serialize(service.Capture(), json);
                if (Option("--output") is { } output) File.WriteAllText(output, result); else Console.WriteLine(result);
                return;
            }
            using var instance = new Mutex(true, "Local\\DeskFrz-" + Fingerprint.Of(Path.GetFullPath(data)), out var first);
            if (!first) { SingleInstance.ActivateExisting(data); return; }
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var updates = new UpdateService(data, !args.Contains("--serve") && !uiSelfTest);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
                Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:" + (Option("--port") ?? "0"));
            var host = builder.Build();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string baseUrl = "";
            DashboardHost? dashboard = null;
            TrayIcon? tray = null;
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
            host.MapGet("/api/health", () => Results.Json(new { mode = args.Contains("--serve") ? "browser" : "native", nativeReady = dashboard?.IsReady == true }));
            host.MapPost("/api/activate", () => OnDesktop(() =>
            {
                if (tray == null) throw new InvalidOperationException("当前实例为开发服务，没有应用窗口。");
                tray.ShowWindow();
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
            host.MapPost("/api/settings", (SettingsRequest request) => OnDesktop(() => service.Settings(request.PanelGap, request.CheckUpdates, request.BackgroundEnabled)));
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
                    await application.Dispatcher.InvokeAsync(() => {
                        if (tray is null) application.Shutdown();
                        else tray.Exit();
                    });
                });
                return Results.Json(new { installing = true });
            });
            var screenWatcher = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            bool monitoring = false;
            void ObserveDesktop() {
                if (!monitoring) return;
                try {
                    service.Observe();
                    if (!service.HasPendingReview) tray?.ClearNotice();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                { System.Diagnostics.Trace.TraceWarning("Desktop observation deferred: {0}", ex.Message); }
            }
            EventHandler displayChanged = (_, _) => application.Dispatcher.BeginInvoke((Action)ObserveDesktop);
            void SetMonitoring(bool enabled) {
                tray?.SetBackgroundEnabled(enabled);
                if (!enabled) dashboard?.CancelChangeReview();
                if (monitoring == enabled) return;
                monitoring = enabled;
                if (enabled) {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanging += displayChanged;
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayChanged;
                    ObserveDesktop(); screenWatcher.Start();
                } else {
                    screenWatcher.Stop();
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanging -= displayChanged;
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayChanged;
                }
            }
            void QueueNotice(ReviewKind kind, string title, string message) {
                application.Dispatcher.BeginInvoke((Action)(() => {
                    // A user can disable monitoring after this callback was queued.
                    if (monitoring && service.HasPendingReview) tray?.Notify(title, message, kind);
                }));
            }
            service.ScreenSwitched += () => QueueNotice(ReviewKind.Screen, "屏幕布局已改变", "点击查看桌面布局与恢复选项。");
            service.DesktopChanged += () => QueueNotice(ReviewKind.Desktop, "桌面图标已变动", "是否保存当前布局？点击选择是否保存。");
            service.RecoveryFailed += () => QueueNotice(ReviewKind.Recovery, "默认单屏恢复未完成", "点击查看原因与恢复选项。");
            service.BackgroundModeChanged += SetMonitoring;
            screenWatcher.Tick += (_, _) => ObserveDesktop();
            host.StartAsync().GetAwaiter().GetResult();
            baseUrl = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "session.json"), JsonSerializer.Serialize(new { url = baseUrl, processId = Environment.ProcessId }, json));
            // Async host disposal must not capture the exiting WPF dispatcher.
            try
            {
                if (args.Contains("--serve")) {
                    SetMonitoring(store.Read().Settings.BackgroundEnabled);
                    Console.WriteLine(baseUrl); application.Run();
                }
                else
                {
                    var window = new Window { Title = uiSelfTest ? "DeskFrzA · 合成桌面自检" : "DeskFrzA", Width = 1380, Height = 920,
                        MinWidth = 920, MinHeight = 640, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#F8F6EF")! };
                    using var trayIcon = new TrayIcon(application, window, data, store.Read().Settings.BackgroundEnabled);
                    tray = trayIcon;
                    void WindowFailure(Exception ex) {
                        if ((args.Contains("--lifecycle-test") || args.Contains("--memory-test") || args.Contains("--mode-test")) && Option("--output") is { } output)
                            File.WriteAllText(output, JsonSerializer.Serialize(new { error = ex.ToString() }, json));
                        else if (dashboard?.HasLoaded == true && trayIcon.BackgroundEnabled) {
                            MessageBox.Show("后台保护仍在运行，请从托盘重新打开界面。\n\n" + ex.Message, "DeskFrzA 界面加载失败");
                            return;
                        }
                        else MessageBox.Show(ex.Message, "DeskFrzA 启动失败");
                        trayIcon.Exit(1);
                    }
                    using var page = new DashboardHost(window, baseUrl, data, args.Contains("--dev"), WindowFailure);
                    dashboard = page;
                    trayIcon.Opened += page.EnsureForegroundView;
                    trayIcon.ReviewRequested += kind => { service.AcknowledgeReview(kind); page.RequestChangeReview(kind); };
                    SetMonitoring(store.Read().Settings.BackgroundEnabled);
                    bool tested = false;
                    window.Loaded += async (_, _) => {
                        if (tested) return;
                        tested = true;
                        try {
                            if (args.Contains("--memory-test"))
                                await MemoryTests.Run(window, trayIcon, () => page.IsReady, data, Option("--output")!);
                            else if (args.Contains("--lifecycle-test"))
                                await SingleInstance.TestLifecycle(window, trayIcon, data, Option("--output")!);
                            else if (args.Contains("--mode-test"))
                                await SingleInstance.TestModes(window, trayIcon, service, store, data, Option("--output")!);
                        }
                        catch (Exception ex) { WindowFailure(ex); }
                    };
                    application.Run(window);
                }
            }
            finally
            {
                screenWatcher.Stop();
                service.BackgroundModeChanged -= SetMonitoring;
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
    public record SettingsRequest(int PanelGap, bool? CheckUpdates = null, bool? BackgroundEnabled = null);
    public record UpdateRequest(string Version);
}
