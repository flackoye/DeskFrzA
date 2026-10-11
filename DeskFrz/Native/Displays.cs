using System.Runtime.InteropServices;

namespace DeskFrz.Native;

public static class Displays
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Source { public Luid Adapter; public uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Target { public Luid Adapter; public uint Id, Mode, Output,
        Rotation, Scaling, RateNumerator, RateDenominator, Scanline; public int Available; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SourceName { public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TargetName { public Header Header;
        public uint Flags, Output; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path; }
    delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, nint modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetSourceName(ref SourceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetTargetName(ref TargetName name);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint pathCount, [In] DisplayPath[] paths,
        uint modeCount, nint modes, uint flags);
    public sealed class Configuration
    {
        internal DisplayPath[] Paths = [];
        internal byte[] Modes = [];
        internal List<Display> Screens = [];
        public bool Supports(List<Display> current) => Screens.Select(s => s.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(current.Select(s => s.Id).Order(StringComparer.Ordinal));
        public void Validate(List<Display> current) => Set(current, false);
        public void Restore(List<Display> current) => Set(current, true);
        void Set(List<Display> current, bool apply)
        {
            if (!Supports(current)) throw new InvalidOperationException("屏幕连接已变化，无法撤销切换。");
            var modes = Marshal.AllocHGlobal(Modes.Length);
            try
            {
                Marshal.Copy(Modes, 0, modes, Modes.Length);
                // First validate the exact captured topology; never allow Windows to substitute modes.
                Check(SetDisplayConfig((uint)Paths.Length, Paths, (uint)(Modes.Length / 64), modes, 0x20 | 0x40));
                if (apply) Check(SetDisplayConfig((uint)Paths.Length, Paths, (uint)(Modes.Length / 64), modes, 0x20 | 0x80 | 0x200));
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
    }
    public static Configuration CaptureConfiguration(List<Display> expected)
    {
        if (Fingerprint.Of(Read()) != Fingerprint.Of(expected)) throw new InvalidOperationException("屏幕正在切换，请稍后重试。");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount));
            var paths = new DisplayPath[pathCount];
            var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                int status = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
                if (status == 122) continue;
                Check(status);
                var bytes = new byte[checked((int)modeCount * 64)]; Marshal.Copy(modes, bytes, 0, bytes.Length);
                if (Fingerprint.Of(Read()) != Fingerprint.Of(expected)) throw new InvalidOperationException("屏幕正在切换，请稍后重试。");
                return new Configuration { Paths = paths.Take((int)pathCount).ToArray(), Modes = bytes, Screens = expected.ToList() };
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("屏幕正在切换，请稍后重试。");
    }

    public static List<Display> Read()
    {
        var identities = Identities();
        List<Display> result = [];
        Exception? failure = null;
        MonitorCallback callback = (handle, _, _, _) =>
        {
            try
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfoW(handle, ref info)) throw new System.ComponentModel.Win32Exception();
                Marshal.ThrowExceptionForHR(GetScaleFactorForMonitor(handle, out var scale));
                if (!identities.TryGetValue(info.Device, out var targets) || targets.Count == 0)
                    throw new InvalidOperationException("无法确认显示器身份，请刷新屏幕信息。");
                var target = targets[0];
                var identity = string.Join('|', targets.Select(t => t.Path).Order(StringComparer.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("显示器没有可用的设备身份。");
                result.Add(new Display(Fingerprint.Of(identity.ToUpperInvariant()),
                    string.IsNullOrWhiteSpace(target.Name) ? info.Device : target.Name, info.Device,
                    (info.Flags & 1) != 0, ToArea(info.Bounds), ToArea(info.Work), scale, target.Rotation, targets.Count));
                return true;
            }
            catch (Exception ex) { failure = ex; return false; }
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0))
            throw failure ?? new System.ComponentModel.Win32Exception();
        if (result.Count == 0 || result.Count(m => m.Primary) != 1)
            throw new InvalidOperationException("无法识别当前桌面主屏。");
        if (result.Select(m => m.Id).Distinct().Count() != result.Count)
            throw new InvalidOperationException("显示器身份重复，无法安全匹配。");
        return result.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    record Identity(string Name, string Path, uint Rotation);
    static Dictionary<string, List<Identity>> Identities()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount));
            var paths = new DisplayPath[pathCount];
            var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                int status = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
                if (status == 122) continue;
                Check(status);
                var result = new Dictionary<string, List<Identity>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths.Take((int)pathCount))
                {
                    var source = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(),
                        Adapter = path.Source.Adapter, Id = path.Source.Id }, Name = "" };
                    var target = new TargetName { Header = new Header { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(),
                        Adapter = path.Target.Adapter, Id = path.Target.Id }, Name = "", Path = "" };
                    Check(GetSourceName(ref source)); Check(GetTargetName(ref target));
                    if (!result.TryGetValue(source.Name, out var list)) result[source.Name] = list = [];
                    list.Add(new Identity(target.Name, target.Path, path.Target.Rotation));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("屏幕配置正在变化，请稍后刷新。");
    }
    static void Check(int status) { if (status != 0) throw new System.ComponentModel.Win32Exception(status); }
    static Area ToArea(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
