using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DeskFrz;

/// <summary>Opt-in native window measurements using only the synthetic desktop.</summary>
internal static class MemoryTests
{
    public static async Task Run(Window window, TrayIcon tray, Func<bool> ready, string data, string output)
    {
        async Task<double> WaitForPage()
        {
            var elapsed = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!ready()) {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("内存自检等待界面就绪超时。");
                await Task.Delay(100);
            }
            double milliseconds = elapsed.Elapsed.TotalMilliseconds;
            await Task.Delay(1500);
            return milliseconds;
        }
        var samples = new List<object>();
        var reopenMilliseconds = new List<double>();
        await WaitForPage();
        samples.Add(Sample("visible"));
        for (int cycle = 1; cycle <= 3; cycle++) {
            window.Close();
            await Task.Delay(5000);
            var first = Sample("hidden-" + cycle);
            await Task.Delay(3000);
            var second = Sample("hidden-idle-" + cycle);
            samples.Add(first); samples.Add(second);
            tray.ShowWindow();
            reopenMilliseconds.Add(await WaitForPage());
            samples.Add(Sample("reopened-" + cycle));
        }
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "session.json")));
        string address = session.RootElement.GetProperty("url").GetString()!;
        bool rebuildRacesPassed = await CheckRebuildRaces(address, data);
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(address + "/api/state");
        response.EnsureSuccessStatusCode();
        using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        bool intact = state.RootElement.GetProperty("current").GetProperty("icons").GetArrayLength() == 70
            && !state.RootElement.GetProperty("changed").GetBoolean();
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            serverGC = GCSettings.IsServerGC, cycles = 3, syntheticDesktopIntact = intact, rebuildRacesPassed, reopenMilliseconds, samples
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        if (!intact) throw new InvalidOperationException("内存自检中的合成桌面或比较基准发生变化。");
        tray.Exit();
    }

    static async Task<bool> CheckRebuildRaces(string address, string data)
    {
        var pending = new TaskCompletionSource<CoreWebView2Environment>();
        int attempts = 0, failures = 0;
        var window = new Window { Title = "DeskFrzA · 界面重建自检", Width = 920, Height = 640 };
        using var page = new DashboardHost(window, address, data, false, _ => failures++, () => ++attempts switch {
            1 => pending.Task,
            2 or 4 => Task.FromException<CoreWebView2Environment>(new InvalidOperationException("injected environment failure")),
            _ => CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "webview"))
        });
        async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!condition()) {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("界面重建竞态自检超时。");
                await Task.Delay(50);
            }
        }
        try {
            window.Show();
            window.Hide();
            await Task.Delay(2300);
            pending.SetException(new InvalidOperationException("stale initialization"));
            await Task.Delay(100);
            if (failures != 0) throw new InvalidOperationException("已释放页面的初始化错误影响了当前页面。");
            window.Show();
            await WaitUntil(() => failures == 1);
            page.EnsureForegroundView();
            await WaitUntil(() => page.IsReady);
            window.Hide();
            await Task.Delay(2300);
            window.Show();
            await WaitUntil(() => failures == 2);
            if (!page.HasLoaded) throw new InvalidOperationException("页面重建失败丢失了已启动状态。");
            page.EnsureForegroundView();
            await WaitUntil(() => page.IsReady);
            return true;
        }
        finally { window.Close(); }
    }

    static MemorySample Sample(string phase)
    {
        var parents = new List<(int Id, int Parent)>();
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new System.ComponentModel.Win32Exception();
        try {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception();
            do { parents.Add(((int)entry.ProcessId, (int)entry.ParentProcessId)); }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var ids = new HashSet<int> { Environment.ProcessId };
        bool added;
        do {
            added = false;
            foreach (var process in parents)
                if (ids.Contains(process.Parent) && ids.Add(process.Id)) added = true;
        } while (added);
        var rows = new List<ProcessMemory>();
        foreach (int id in ids) {
            try {
                using var process = Process.GetProcessById(id);
                process.Refresh();
                rows.Add(new(id, process.ProcessName, process.WorkingSet64, process.PrivateMemorySize64,
                    process.TotalProcessorTime.TotalSeconds));
            }
            catch (ArgumentException) { } // A child may have exited since the snapshot.
            catch (InvalidOperationException) { }
        }
        return new(phase, DateTimeOffset.Now, GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes,
            rows.Sum(p => p.WorkingSetBytes), rows.Sum(p => p.PrivateBytes), rows);
    }

    record ProcessMemory(int Id, string Name, long WorkingSetBytes, long PrivateBytes, double CpuSeconds);
    record MemorySample(string Phase, DateTimeOffset At, long ManagedHeapBytes, long ManagedCommittedBytes,
        long TotalWorkingSetBytes, long TotalPrivateBytes, List<ProcessMemory> Processes);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(nint handle);
}
