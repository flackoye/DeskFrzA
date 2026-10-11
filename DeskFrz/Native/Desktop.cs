using System.Runtime.InteropServices;

namespace DeskFrz.Native;

/// <summary>All calls belong to the application's STA dispatcher. Slots follow Windows SDK COM headers.</summary>
public sealed class Desktop : IDisposable
{
    readonly List<nint> owned = [];
    readonly List<nint> pidls = [];
    readonly object windows;
    readonly object dispatch;
    readonly nint view, shellView;
    readonly Dictionary<string, nint> items = new(StringComparer.OrdinalIgnoreCase);
    static readonly Guid FolderView2 = new("1af3a467-214f-4298-908e-06b03e0b39f9");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryService(nint self, ref Guid service, ref Guid iid, out nint output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPointer(nint self, out nint output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetCount(nint self, uint flags, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPidl(nint self, int index, out nint pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetItem(nint self, int index, ref Guid iid, out nint output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetName(nint self, uint kind, out nint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPosition(nint self, nint pidl, out Displays.Point point);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPoint(nint self, out Displays.Point point);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFlags(nint self, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetFlags(nint self, uint mask, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetIconSize(nint self, out uint mode, out int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PositionItems(nint self, uint count,
        [In] nint[] pidls, [In] Displays.Point[] positions, uint flags);

    public Desktop()
    {
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9ba05972-f6a8-11cf-a442-00a0c90a8f39"), true)!)!;
            object location = 0, empty = null!;
            int hwnd;
            dispatch = ((dynamic)windows).FindWindowSW(ref location, ref empty, 8, out hwnd, 1);
            if (dispatch == null) throw new InvalidOperationException("Windows 桌面尚未就绪。");
            var unknown = Own(Marshal.GetIUnknownForObject(dispatch));
            var provider = Query(unknown, new Guid("6d5140c1-7436-11ce-8034-00aa006009fa"));
            var service = new Guid("4c96be40-915c-11cf-99d3-00aa004ae837");
            var iid = new Guid("000214e2-0000-0000-c000-000000000046");
            Check(Method<QueryService>(provider, 3)(provider, ref service, ref iid, out var browser));
            Own(browser);
            Check(Method<GetPointer>(browser, 15)(browser, out var active));
            shellView = Own(active);
            view = Query(shellView, FolderView2);
        }
        catch { Dispose(); throw; }
    }

    public Snapshot Capture()
    {
        var displays = Displays.Read();
        Check(Method<GetPointer>(shellView, 3)(shellView, out var hwnd));
        var origin = new Displays.Point();
        if (!Displays.ClientToScreen(hwnd, ref origin)) throw new System.ComponentModel.Win32Exception();
        Check(Method<GetPoint>(view, 12)(view, out var spacing));
        Check(Method<GetFlags>(view, 25)(view, out var flags));
        Check(Method<GetIconSize>(view, 36)(view, out _, out var size));
        Check(Method<GetCount>(view, 7)(view, 2, out var count));
        foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        pidls.Clear(); items.Clear();
        List<DesktopIcon> icons = [];
        var itemIid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        for (int index = 0; index < count; index++)
        {
            Check(Method<GetPidl>(view, 6)(view, index, out var pidl));
            pidls.Add(pidl);
            Check(Method<GetPosition>(view, 11)(view, pidl, out var p));
            Check(Method<GetItem>(view, 29)(view, index, ref itemIid, out var item));
            string name, id;
            try { id = Name(item, 0x80028000); name = Name(item, 0); }
            finally { Marshal.Release(item); }
            if (!items.TryAdd(id, pidl)) throw new InvalidOperationException("桌面项目身份重复，无法安全保存。");
            var screen = new Position(p.X + origin.X, p.Y + origin.Y);
            var monitor = displays.SingleOrDefault(m => m.Bounds.Contains(screen));
            if (monitor is null) throw new InvalidOperationException($"图标“{name}”位于屏幕外，请先整理后刷新。");
            icons.Add(new DesktopIcon(id, name, monitor.Id, screen.X, screen.Y));
        }
        var secondRead = Displays.Read();
        var grid = new Position(spacing.X, spacing.Y);
        if (grid.X <= 0 || grid.Y <= 0) throw new InvalidOperationException("无法读取桌面图标间距。");
        var key = Fingerprint.Configuration(displays, grid, size, flags);
        if (key != Fingerprint.Configuration(secondRead, grid, size, flags))
            throw new InvalidOperationException("屏幕配置在读取时发生变化，请重试。");
        return new Snapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, key, displays,
            icons, grid, new Position(origin.X, origin.Y), size, flags);
    }

    public void DisableAutoArrange()
    {
        var options = Query(shellView, new Guid("3cc974d2-b302-4d36-ad3e-06d93f695d3f"));
        Check(Method<SetFlags>(options, 3)(options, 2, 2)); // FVO_CUSTOMPOSITION
        Check(Method<SetFlags>(view, 24)(view, 1, 0)); // FWF_AUTOARRANGE only; preserve grid.
        Check(Method<GetFlags>(view, 25)(view, out var flags));
        if ((flags & 1) != 0) throw new InvalidOperationException("自动排列未关闭，请在桌面右键菜单中关闭后重试。");
    }

    public void SetGridAlignment(bool enabled)
    {
        Check(Method<SetFlags>(view, 24)(view, 4, enabled ? 4u : 0u));
        Check(Method<GetFlags>(view, 25)(view, out var flags));
        if (((flags & 4) != 0) != enabled) throw new InvalidOperationException("无法更改对齐到网格设置。");
    }

    public void Apply(List<Move> moves, Position origin)
    {
        if (moves.Count == 0) return;
        Check(Method<GetFlags>(view, 25)(view, out var flags));
        if ((flags & 1) != 0) throw new InvalidOperationException("请先关闭自动排列。");
        var pointers = moves.Select(m => items.TryGetValue(m.Id, out var p) ? p :
            throw new InvalidOperationException("桌面项目发生变化，请重新预检。")).ToArray();
        var positions = moves.Select(m => new Displays.Point { X = m.X - origin.X, Y = m.Y - origin.Y }).ToArray();
        Check(Method<PositionItems>(view, 16)(view, (uint)moves.Count, pointers, positions, 0x80));
    }

    static string Name(nint item, uint kind)
    {
        Check(Method<GetName>(item, 5)(item, kind, out var pointer));
        try { return Marshal.PtrToStringUni(pointer) ?? throw new InvalidOperationException("无法读取桌面项目身份。"); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    nint Own(nint p) { owned.Add(p); return p; }
    nint Query(nint p, Guid iid) { Check(Marshal.QueryInterface(p, ref iid, out var output)); return Own(output); }
    static T Method<T>(nint p, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), slot * nint.Size));
    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    public void Dispose()
    {
        foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        pidls.Clear();
        for (int i = owned.Count - 1; i >= 0; i--) Marshal.Release(owned[i]);
        owned.Clear();
        if (dispatch is not null && Marshal.IsComObject(dispatch)) Marshal.FinalReleaseComObject(dispatch);
        if (windows is not null && Marshal.IsComObject(windows)) Marshal.FinalReleaseComObject(windows);
    }
}
