using DeskFrz.Native;

namespace DeskFrz;

public sealed class DesktopService(Store store)
{
    readonly ScreenSwitchTracker switches = new();
    readonly SingleScreenRecovery singleRecovery = new();
    bool restoringSingle;
    string? singleRecoveryMessage;
    bool singleRecoveryFailed;
    Displays.Configuration? previousConfiguration, switchConfiguration;
    public Snapshot Capture() { using var dpi = new DpiScope(); using var desktop = new Desktop(); return desktop.Capture(); }
    public event Action? ScreenSwitched;
    public void Observe() => Observe(Capture());
    void Observe(Snapshot current)
    {
        var library = store.Read();
        var defaultSingle = library.Records.SingleOrDefault(s => s.Id == library.DefaultSingleId && s.IsSingleScreen);
        if (!restoringSingle && singleRecovery.Ready(current, defaultSingle?.Id, DateTimeOffset.UtcNow))
        {
            restoringSingle = true;
            try {
                Restore(defaultSingle!.Id, current.ConfigKey, ContentKey(current));
                singleRecoveryMessage = "已恢复默认单屏布局";
                singleRecoveryFailed = false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) {
                singleRecoveryMessage = "单屏恢复未完成：" + ex.Message;
                singleRecoveryFailed = true;
            }
            finally { restoringSingle = false; }
            return;
        }
        if (current.Displays.Count != 1) { singleRecoveryMessage = null; singleRecoveryFailed = false; }
        Displays.Configuration? configuration = null;
        try { configuration = Displays.CaptureConfiguration(current.Displays); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
            System.Diagnostics.Trace.TraceWarning("Screen backup unavailable: {0}", ex.Message);
        }
        var previousSource = switches.Source;
        switches.Observe(current);
        bool switched = switches.Source is not null && previousSource?.Id != switches.Source.Id;
        if (switched) switchConfiguration = previousConfiguration;
        previousConfiguration = configuration;
        // Disconnect recovery takes precedence over the primary-switch migration prompt.
        if (switched && current.Displays.Count > 1) ScreenSwitched?.Invoke();
    }
    public object State()
    {
        var current = Capture();
        Observe(current);
        current = Capture(); // Observe may have restored the default single-screen template.
        var library = store.Read();
        if (!library.SelectionInitialized)
        {
            var matching = library.Records.OrderByDescending(s => s.Number).FirstOrDefault(s => !HasChanged(s, current));
            library.ActiveId = matching?.Id;
            library.ActiveBaseline = matching;
            library.SelectionInitialized = true;
            store.Write(library);
        }
        var saved = library.Records.SingleOrDefault(s => s.Id == library.ActiveId);
        var baseline = library.ActiveBaseline;
        return new { current, saved, switchSource = current.Displays.Count == 1 ? null : switches.Source, canUndoSwitch = switches.Source is not null && switchConfiguration?.Supports(current.Displays) == true, records = library.Records.OrderByDescending(s => s.Number),
            defaultSingleId = library.DefaultSingleId, needsSingleSetup = !library.Records.Any(s => s.Id == library.DefaultSingleId && s.IsSingleScreen), singleRecoveryMessage, singleRecoveryFailed, singleRecoveryPending = singleRecovery.Pending,
            changed = baseline is not null && HasChanged(baseline, current), baselineKey = baseline?.ConfigKey, baselinePrimaryId = baseline?.Displays.Single(s => s.Primary).Id, desktopKey = ContentKey(current),
            canUndo = library.Undo is not null, settings = library.Settings,
            plan = saved is null ? null : Build(saved, current) };
    }
    public object Save(bool single = false) {
        var current = Capture();
        var library = store.Read();
        if (!single && !library.Records.Any(s => s.Id == library.DefaultSingleId && s.IsSingleScreen))
            throw new InvalidOperationException("请先保存默认单屏布局。");
        store.Save(single ? SingleScreenLayouts.Create(current) : current);
        if (single) { var savedLibrary = store.Read(); savedLibrary.ActiveBaseline = current; store.Write(savedLibrary); }
        if (single && !library.Records.Any(s => s.Id == library.DefaultSingleId && s.IsSingleScreen)) store.SetDefaultSingle(current.Id);
        switches.Clear(current); switchConfiguration = null; return State();
    }
    public object DefaultSingle(string? id) {
        var saved = id is null ? Capture() : Find(id);
        if (!saved.IsSingleScreen) {
            saved = SingleScreenLayouts.Create(saved) with { Id = Guid.NewGuid().ToString("N"), SavedAt = DateTimeOffset.Now,
                Name = string.IsNullOrEmpty(saved.Name) ? "单屏布局" : saved.Name + " · 单屏" };
            store.Save(saved, false);
        } else if (id is null) store.Save(saved);
        store.SetDefaultSingle(saved.Id);
        return State();
    }
    public object RetrySingleRecovery() { singleRecovery.Retry(); return State(); }
    public object Rename(string id, string name) { store.Rename(id, name); return State(); }
    public object Delete(string id) { store.Delete(id); return State(); }
    public object DisableAutoArrange()
    {
        using var dpi = new DpiScope();
        using var desktop = new Desktop();
        desktop.DisableAutoArrange();
        return State();
    }
    public object Settings(int gap, bool? checkUpdates = null)
    {
        if (gap is < 24 or > 96) throw new ArgumentException("层间距应在 24–96 像素之间。");
        var library = store.Read(); library.Settings = library.Settings with { PanelGap = gap, CheckUpdates = checkUpdates ?? library.Settings.CheckUpdates }; store.Write(library);
        return library.Settings;
    }
    public object Restore(string id, string expectedKey, string expectedDesktop, bool undo = false, string? targetId = null, bool allowResize = false, bool allowFreePlacement = false)
    {
        using var dpi = new DpiScope();
        var saved = undo ? store.Read().Undo ?? throw new InvalidOperationException("没有可撤销的恢复。") : Find(id);
        using var desktop = new Desktop();
        var before = desktop.Capture();
        if (before.ConfigKey != expectedKey || ContentKey(before) != expectedDesktop)
            throw new InvalidOperationException("桌面或屏幕在预检后发生变化，请重新预检。");
        var plan = !undo && targetId is not null ? TransferPlanner.Build(saved, before, targetId, allowResize) : Build(saved, undo ? WithSavedGrid(saved, before) : before);
        if (plan.BlockedReason is not null) throw new InvalidOperationException(plan.BlockedReason);
        if (plan.Transfer?.FreePlacement == true && !allowFreePlacement)
            throw new InvalidOperationException("保留原间距需要关闭对齐到网格，请确认后迁移。");
        var library = store.Read();
        if (!undo)
        {
            library.Undo = before;
            library.UndoActiveId = library.ActiveId;
            library.UndoActiveBaseline = library.ActiveBaseline;
        }
        store.Write(library); // Durable backup MUST precede the first native mutation.
        bool restoreGrid = undo || (targetId is null && saved.IsSingleScreen && before.Displays.Count == 1);
        if (plan.Transfer?.FreePlacement == true) desktop.SetGridAlignment(false);
        else if (restoreGrid && (saved.FolderFlags & 4) != (before.FolderFlags & 4)) desktop.SetGridAlignment((saved.FolderFlags & 4) != 0);
        var placementBase = desktop.Capture();
        uint expectedFlags = plan.Transfer?.FreePlacement == true ? before.FolderFlags & ~4u
            : restoreGrid ? (before.FolderFlags & ~4u) | (saved.FolderFlags & 4) : before.FolderFlags;
        if (placementBase.ConfigKey != Fingerprint.Configuration(before.Displays, before.Spacing, before.IconSize, expectedFlags))
            throw new InvalidOperationException("屏幕在迁移前发生变化，已暂停并保留撤销备份。");
        desktop.Apply(plan.Moves, placementBase.ViewOrigin);
        Thread.Sleep(180); // Shell positioning is asynchronous; verify returned positions.
        var after = desktop.Capture();
        if (after.ConfigKey != placementBase.ConfigKey)
            throw new InvalidOperationException("恢复时屏幕配置发生变化。已保留恢复前备份，请检查桌面。");
        if (!Verifier.Matches(placementBase, plan, after))
            throw new InvalidOperationException("Windows 未按计划放置全部图标。已保留恢复前备份，可点击撤销。");
        library.ActiveId = undo ? library.UndoActiveId : library.Records.Any(s => s.Id == saved.Id) ? saved.Id : null;
        library.SelectionInitialized = true;
        library.ActiveBaseline = undo ? library.UndoActiveBaseline : after;
        if (undo) { library.Undo = null; library.UndoActiveId = null; library.UndoActiveBaseline = null; }
        store.Write(library);
        switches.Clear(after); switchConfiguration = null;
        return State();
    }
    public object UndoScreenSwitch(string expectedKey, string expectedDesktop)
    {
        using var dpi = new DpiScope();
        var source = switches.Source ?? throw new InvalidOperationException("没有切换前的记录。");
        var configuration = switchConfiguration ?? throw new InvalidOperationException("无法撤销本次屏幕切换。");
        var before = Capture();
        if (before.ConfigKey != expectedKey || ContentKey(before) != expectedDesktop)
            throw new InvalidOperationException("桌面已变化，请重新打开提示。");
        configuration.Validate(before.Displays);
        var library = store.Read();
        library.Undo = before; library.UndoActiveId = library.ActiveId; library.UndoActiveBaseline = library.ActiveBaseline;
        store.Write(library);
        configuration.Restore(before.Displays);
        Snapshot settled = before;
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(100); settled = Capture();
            if (settled.ConfigKey == source.ConfigKey) break;
        }
        if (settled.ConfigKey != source.ConfigKey)
            throw new InvalidOperationException("原主屏已恢复，但桌面设置仍有差异，请从历史记录恢复布局。");
        var plan = Planner.Build(source, settled);
        if (plan.BlockedReason is not null) throw new InvalidOperationException(plan.BlockedReason);
        using (var desktop = new Desktop()) desktop.Apply(plan.Moves, settled.ViewOrigin);
        Thread.Sleep(180); var after = Capture();
        if (after.ConfigKey != settled.ConfigKey || !Verifier.Matches(settled, plan, after))
            throw new InvalidOperationException("原主屏已恢复，部分图标未按计划落位，请从历史记录恢复布局。");
        library.ActiveBaseline = after; store.Write(library);
        switches.Clear(after); switchConfiguration = null;
        return State();
    }
    public object UndoPreview()
    {
        var saved = store.Read().Undo ?? throw new InvalidOperationException("没有可撤销的恢复。");
        var current = Capture();
        return new { saved, plan = Planner.Build(saved, WithSavedGrid(saved, current)) with { CurrentKey = current.ConfigKey }, current, desktopKey = ContentKey(current) };
    }
    public object PreviewWithState(string id, string? targetId = null, bool allowResize = false)
    {
        var current = Capture();
        var saved = Find(id);
        return new { saved, plan = targetId is not null ? TransferPlanner.Build(saved, current, targetId, allowResize) : Build(saved, current), current, desktopKey = ContentKey(current) };
    }
    static RestorePlan Build(Snapshot saved, Snapshot current) => saved.IsSingleScreen
        ? SingleScreenLayouts.Build(saved, current) : Planner.Build(saved, current);
    static Snapshot WithSavedGrid(Snapshot saved, Snapshot current) => SingleScreenLayouts.WithSavedGrid(saved, current);
    Snapshot Find(string id)
    {
        if (switches.Source?.Id == id) return switches.Source;
        var library = store.Read();
        return library.Current.Concat(library.History).SingleOrDefault(s => s.Id == id)
            ?? throw new InvalidOperationException("未找到该布局记录。");
    }
    public static bool HasChanged(Snapshot baseline, Snapshot current) => baseline.ConfigKey != current.ConfigKey || ContentKey(baseline) != ContentKey(current);
    public static string ContentKey(Snapshot s) => Fingerprint.Of(s.Icons.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
        .Select(i => new { i.Id, i.MonitorId, i.X, i.Y }));
}
