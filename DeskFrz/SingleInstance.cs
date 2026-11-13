using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace DeskFrz;

internal static class SingleInstance
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public static void ActivateExisting(string data)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // The first instance may still be writing its session during startup.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        Exception? failure = null;
        do
        {
            try
            {
                using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "session.json")));
                var url = new Uri(session.RootElement.GetProperty("url").GetString()!);
                if (url.Host != "127.0.0.1" || url.Scheme != "http") throw new InvalidDataException("无效的本地会话地址。");
                AllowSetForegroundWindow(session.RootElement.GetProperty("processId").GetUInt32());
                using var response = client.GetAsync(new Uri(url, "/api/session")).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var credentials = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "/api/activate"));
                request.Headers.Add("X-DeskFrz-Token", credentials.RootElement.GetProperty("token").GetString());
                using var activated = client.Send(request);
                activated.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
            { failure = ex; Thread.Sleep(200); }
        } while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException("已有 DeskFrzA 进程，但窗口未响应。请在任务管理器结束 DeskFrzA 后重新打开。", failure);
    }

    public static async Task TestLifecycle(Window window, TrayIcon tray, string data, string output)
    {
        window.Close();
        await Task.Delay(3500); // The two-second dashboard release must have completed.
        bool hidden = !window.IsVisible && tray.IsVisible;
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "session.json")));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var state = await client.GetAsync(session.RootElement.GetProperty("url").GetString() + "/api/state");
        bool backgroundResponsive = state.IsSuccessStatusCode;
        using var healthResponse = await client.GetAsync(session.RootElement.GetProperty("url").GetString() + "/api/health");
        healthResponse.EnsureSuccessStatusCode();
        using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
        bool dashboardReleased = !health.RootElement.GetProperty("nativeReady").GetBoolean();
        tray.ShowWindow();
        bool trayRestored = window.IsVisible;
        window.Close();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--data"); start.ArgumentList.Add(data);
        using var duplicate = Process.Start(start)!;
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        bool restored = window.IsVisible && window.WindowState == WindowState.Normal;
        window.WindowState = WindowState.Minimized;
        tray.ShowWindow();
        bool minimizedRestored = window.WindowState == WindowState.Normal;
        File.WriteAllText(output, JsonSerializer.Serialize(new { hidden, backgroundResponsive, dashboardReleased, trayRestored,
            duplicateExitCode = duplicate.ExitCode, restored, minimizedRestored, active = window.IsActive }));
        if (!hidden || !backgroundResponsive || !dashboardReleased || !trayRestored || duplicate.ExitCode != 0 || !restored || !minimizedRestored)
            throw new InvalidOperationException("托盘窗口生命周期验证失败。");
        tray.Exit();
    }

    public static async Task TestModes(Window window, TrayIcon tray, DesktopService service, Store store, string data, string output)
    {
        bool defaultOff = !tray.BackgroundEnabled && !tray.IsVisible
            && Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose && !store.Read().Settings.BackgroundEnabled;
        var settings = store.Read().Settings;
        service.Settings(settings.PanelGap, backgroundEnabled: true);
        bool enabledSaved = new Store(data).Read().Settings.BackgroundEnabled && tray.IsVisible;
        window.Close();
        await Task.Delay(3500);
        bool residentHidden = !window.IsVisible;
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "session.json")));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(session.RootElement.GetProperty("url").GetString() + "/api/state");
        bool backendAlive = response.IsSuccessStatusCode;
        service.Settings(settings.PanelGap, backgroundEnabled: false);
        bool disabledSaved = !new Store(data).Read().Settings.BackgroundEnabled && !tray.IsVisible && window.IsVisible
            && Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose;
        File.WriteAllText(output, JsonSerializer.Serialize(new { defaultOff, enabledSaved, residentHidden, backendAlive, disabledSaved }));
        if (!defaultOff || !enabledSaved || !residentHidden || !backendAlive || !disabledSaved)
            throw new InvalidOperationException("后台模式切换验证失败。");
        window.Close(); // Off means closing the main window must end the process.
    }
}
