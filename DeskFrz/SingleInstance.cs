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

    public static async Task TestLifecycle(Window window, string data, string output)
    {
        window.WindowState = WindowState.Minimized;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--data"); start.ArgumentList.Add(data);
        using var duplicate = Process.Start(start)!;
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        bool restored = window.IsVisible && window.WindowState == WindowState.Normal;
        File.WriteAllText(output, JsonSerializer.Serialize(new { duplicateExitCode = duplicate.ExitCode, restored, active = window.IsActive }));
        if (duplicate.ExitCode != 0 || !restored) throw new InvalidOperationException("窗口唤回验证失败。");
        window.Close();
    }
}
