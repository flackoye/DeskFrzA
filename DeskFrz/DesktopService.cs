namespace DeskFrz;

public sealed class DesktopService
{
    readonly Store store;
    readonly IDesktopRuntime runtime;
    public DesktopService(Store store) : this(store, new WindowsDesktopRuntime()) { }
    internal DesktopService(Store store, IDesktopRuntime runtime) { this.store = store; this.runtime = runtime; }
    readonly ScreenSwitchTracker switches = new();
    readonly SingleScreenRecovery singleRecovery = new();
    readonly DesktopChangeTracker changes = new();
    bool restoringSingle, screenNoticePending;
    string? singleRecoveryMessage;
    bool singleRecoveryFailed;
    IDisplayConfiguration? previousConfiguration, switchConfiguration;
    public Snapshot Capture() { using var dpi = runtime.EnterDpiScope(); using var desktop = runtime.Open(); return desktop.Capture(); }
    public event Action? ScreenSwitched;
    public event Action? DesktopChanged;
    public event Action? RecoveryFailed;
    public event Action<bool>? BackgroundModeChanged;
    public bool HasPendingReview => changes.IsDirty || screenNoticePending || singleRecoveryFailed;
    internal void AcknowledgeReview(ReviewKind kind) { if (kind == ReviewKind.Screen) screenNoticePending = false; }
    public void Observe()
    {
        var library = store.Read();
        if (library.Settings.BackgroundEnabled) Observe(Capture(), library);
    }
    bool Observe(Snapshot current, Library library)
    {
        var defaultSingle = library.Records.SingleOrDefault(s => s.Id == library.DefaultSingleId && s.IsSingleScreen);
        if (!restoringSingle && singleRecovery.Ready(current, defaultSingle?.Id, runtime.Now))
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
                RecoveryFailed?.Invoke();
            }
            finally { restoringSingle = false; }
            return true;
        }
        if (current.Displays.Count != 1) { singleRecoveryMessage = null; singleRecoveryFailed = false; }
        IDisplayConfiguration? configuration = null;
        try { configuration = runtime.CaptureConfiguration(current.Displays); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
            System.Diagnostics.Trace.TraceWarning("Screen backup unavailable: {0}", ex.Message);
        }
        var previousSource = switches.Source;
        switches.Observe(current);
        bool switched = switches.Source is not null && previousSource?.Id != switches.Source.Id;
        if (switched) switchConfiguration = previousConfiguration;
        else if (switches.Source is null) { switchConfiguration = null; screenNoticePending = false; }
        previousConfiguration = configuration;
        if (switched) { changes.Accept(ContentKey(current)); screenNoticePending = current.Displays.Count > 1; }
        else if (changes.Observe(ContentKey(current), runtime.Now)) {
            if (library.Records.Any(s => !HasChanged(s, current))) changes.Accept(ContentKey(current));
            else DesktopChanged?.Invoke();
        }
        // Disconnect recovery takes precedence over the primary-switch migration prompt.
        if (switched && current.Displays.Count > 1) ScreenSwitched?.Invoke();
        return false;
    }
    public object State()
    {
        var library = store.Read();
        var current = Capture();
        if (library.Settings.BackgroundEnabled && Observe(current, library)) {
            current = Capture(); // Automatic recovery may have changed the desktop and library.
            library = store.Read();
        }
        if (!library.SelectionInitialized || library.ActiveBaseline is null || HasChanged(library.ActiveBaseline, current))
        {
            // Recognize complete saved desktops only. Template matching would swallow
            // later user moves of icons that were absent from the historical template.
            var matching = library.Records.OrderByDescending(s => s.SavedAt).FirstOrDefault(s => !HasChanged(s, current));
            if (!library.SelectionInitialized || matching is not null) {
                library.ActiveId = matching?.Id;
                library.ActiveBaseline = matching is null ? null : current;
                library.SelectionInitialized = true;
                store.Write(library);
            }
        }
        var saved = library.Records.SingleOrDefault(s => s.Id == library.ActiveId);
        var baseline = library.ActiveBaseline;
        return new { current, saved, switchSource = current.Displays.Count == 1 ? null : switches.Source, canUndoSwitch = switches.Source is not null && switchConfiguration?.Supports(current.Displays) == true, records = library.Records.OrderByDescending(s => s.Number),
            defaultSingleId = library.DefaultSingleId, singleRecoveryMessage, singleRecoveryFailed, singleRecoveryPending = singleRecovery.Pending,
            changed = baseline is not null && HasChanged(baseline, current), baselineKey = baseline?.ConfigKey, baselinePrimaryId = baseline?.Displays.Single(s => s.Primary).Id, desktopKey = ContentKey(current),
            canUndo = library.Undo is not null, settings = library.Settings,
            plan = saved is null ? null : Build(saved, current) };
    }
    public object Save(bool single = false) {
        var current = Capture();
        store.Save(single ? SingleScreenLayouts.Create(current) : current, baseline: current);
        CompleteManualAction(current);
        switches.Clear(current); switchConfiguration = null; return State();
    }
    public object DefaultSingle(string? id) {
        var saved = id is null ? Capture() : Find(id);
        var current = id is null ? saved : null;
        if (!saved.IsSingleScreen) {
            saved = SingleScreenLayouts.Create(saved) with { Id = Guid.NewGuid().ToString("N"), SavedAt = DateTimeOffset.Now,
                Name = string.IsNullOrEmpty(saved.Name) ? "单屏布局" : saved.Name + " · 单屏" };
            store.Save(saved, id is null, current);
        } else if (id is null) store.Save(saved);
        store.SetDefaultSingle(saved.Id);
        if (current is not null) CompleteManualAction(current);
        return State();
    }
    public object RetrySingleRecovery() {
        if (!store.Read().Settings.BackgroundEnabled) throw new InvalidOperationException("请先开启后台挂载。");
        singleRecovery.Retry(); singleRecoveryMessage = null; singleRecoveryFailed = false; return State();
    }
    public object Rename(string id, string name) { store.Rename(id, name); return State(); }
    public object Delete(string id) { store.Delete(id); return State(); }
    public object DisableAutoArrange()
    {
        using var dpi = runtime.EnterDpiScope();
        using var desktop = runtime.Open();
        desktop.DisableAutoArrange();
        return State();
    }
    public object Settings(int gap, bool? checkUpdates = null, bool? backgroundEnabled = null)
    {
        if (gap is < 24 or > 96) throw new ArgumentException("层间距应在 24–96 像素之间。");
        var library = store.Read();
        bool modeChanged = backgroundEnabled is bool enabled && enabled != library.Settings.BackgroundEnabled;
        // Capture before persisting so a failed capture cannot half-enable protection.
        var current = modeChanged && backgroundEnabled == true ? Capture() : null;
        library.Settings = library.Settings with { PanelGap = gap, CheckUpdates = checkUpdates ?? library.Settings.CheckUpdates,
            BackgroundEnabled = backgroundEnabled ?? library.Settings.BackgroundEnabled };
        store.Write(library);
        if (modeChanged) {
            singleRecovery.Retry();
            singleRecoveryMessage = null; singleRecoveryFailed = false;
            switches.Reset(); previousConfiguration = switchConfiguration = null;
            screenNoticePending = false;
            changes.Accept(current is null ? null : ContentKey(current));
            // Enabling monitoring is not permission to overwrite the current desktop.
            if (current is not null) singleRecovery.Complete(current, library.DefaultSingleId);
            BackgroundModeChanged?.Invoke(library.Settings.BackgroundEnabled);
        }
        return library.Settings;
    }
    public object Restore(string id, string expectedKey, string expectedDesktop, bool undo = false, string? targetId = null, bool allowResize = false, bool allowFreePlacement = false)
    {
        using var dpi = runtime.EnterDpiScope();
        var saved = undo ? store.Read().Undo ?? throw new InvalidOperationException("没有可撤销的恢复。") : Find(id);
        using var desktop = runtime.Open();
        var before = desktop.Capture();
        if (before.ConfigKey != expectedKey || ContentKey(before) != expectedDesktop)
            throw new InvalidOperationException("桌面或屏幕在预检后发生变化，请重新预检。");
        var plan = BuildOperation(saved, before, undo, targetId, allowResize);
        if (plan.BlockedReason is not null) throw new InvalidOperationException(plan.BlockedReason);
        bool restoresSingleSetting = targetId is null && saved.IsSingleScreen && before.IsSingleScreen
            && plan.GridAlignment == ((saved.FolderFlags & 4) != 0);
        if ((plan.Transfer?.FreePlacement == true || (!undo && !restoresSingleSetting && plan.GridAlignment == false && (before.FolderFlags & 4) != 0)) && !allowFreePlacement)
            throw new InvalidOperationException("保留原间距需要关闭对齐到网格，请确认后恢复。");
        var library = store.Read();
        if (!undo)
        {
            library.Undo = before;
            library.UndoActiveId = library.ActiveId;
            library.UndoActiveBaseline = library.ActiveBaseline;
            library.UndoIconIds = null; // Keep a full emergency backup until placement succeeds.
        }
        store.Write(library); // Durable backup MUST precede the first native mutation.
        bool gridChanged = plan.GridAlignment is bool aligned && aligned != ((before.FolderFlags & 4) != 0);
        if (gridChanged) desktop.SetGridAlignment(plan.GridAlignment!.Value);
        var placementBase = desktop.Capture();
        uint expectedFlags = plan.GridAlignment is bool enableGrid ? (before.FolderFlags & ~4u) | (enableGrid ? 4u : 0u) : before.FolderFlags;
        if (placementBase.ConfigKey != Fingerprint.Configuration(before.Displays, before.Spacing, before.IconSize, expectedFlags, before.SpacingDpi))
            throw new InvalidOperationException("屏幕在迁移前发生变化，已暂停并保留撤销备份。");
        if (!SameIconIds(before, placementBase) || (!(gridChanged && plan.GridAlignment == true) && ContentKey(before) != ContentKey(placementBase)))
            throw new InvalidOperationException("桌面项目在恢复前发生变化，已暂停并保留撤销备份。");
        if (gridChanged && plan.GridAlignment == true) {
            // Enabling the Shell grid may snap newcomers. Replan against their actual
            // positions instead of applying a plan built before the setting changed.
            plan = BuildOperation(saved, placementBase, undo, targetId, allowResize);
            if (plan.BlockedReason is not null) throw new InvalidOperationException(plan.BlockedReason);
        }
        desktop.Apply(plan.Moves, placementBase.ViewOrigin);
        var after = VerifyPlacement(desktop, placementBase, plan);
        library.ActiveId = undo ? library.UndoActiveId : library.Records.Any(s => s.Id == saved.Id) ? saved.Id : null;
        library.SelectionInitialized = true;
        library.ActiveBaseline = undo ? library.UndoActiveBaseline : after;
        if (undo) { library.Undo = null; library.UndoActiveId = null; library.UndoActiveBaseline = null; library.UndoIconIds = null; }
        else {
            var finalIcons=after.Icons.ToDictionary(i=>i.Id,StringComparer.OrdinalIgnoreCase);
            library.UndoIconIds=before.Icons.Where(i=>finalIcons.TryGetValue(i.Id,out var placed)
                && (placed.X!=i.X || placed.Y!=i.Y || placed.MonitorId!=i.MonitorId)).Select(i=>i.Id).ToList();
        }
        store.Write(library);
        changes.Accept(ContentKey(after));
        screenNoticePending = false;
        if (!restoringSingle) CompleteManualAction(after);
        switches.Clear(after); switchConfiguration = null;
        return State();
    }
    public object UndoScreenSwitch(string expectedKey, string expectedDesktop)
    {
        using var dpi = runtime.EnterDpiScope();
        var source = switches.Source ?? throw new InvalidOperationException("没有切换前的记录。");
        var configuration = switchConfiguration ?? throw new InvalidOperationException("无法撤销本次屏幕切换。");
        var before = Capture();
        if (before.ConfigKey != expectedKey || ContentKey(before) != expectedDesktop)
            throw new InvalidOperationException("桌面已变化，请重新打开提示。");
        configuration.Validate(before.Displays);
        var library = store.Read();
        library.Undo = before; library.UndoActiveId = library.ActiveId; library.UndoActiveBaseline = library.ActiveBaseline;
        library.UndoIconIds = null;
        store.Write(library);
        try {
            configuration.Restore(before.Displays);
            // The execution session must capture its own PIDLs, origin, and desktop.
            using var desktop = runtime.Open();
            Snapshot settled = before;
            for (int i = 0; i < 20; i++) {
                runtime.Wait(100); settled = desktop.Capture();
                if (settled.ConfigKey == source.ConfigKey) break;
            }
            if (settled.ConfigKey != source.ConfigKey)
                throw new InvalidOperationException("原主屏已恢复，但桌面设置仍有差异，请从历史记录恢复布局。");
            var plan = Planner.Build(source, settled);
            if (plan.BlockedReason is not null) throw new InvalidOperationException(plan.BlockedReason);
            if (plan.GridAlignment == false && (settled.FolderFlags & 4) != 0)
                throw new InvalidOperationException("原主屏已恢复，当前网格设置无法保留原布局，请从历史记录预览恢复。");
            desktop.Apply(plan.Moves, settled.ViewOrigin);
            var after = VerifyPlacement(desktop, settled, plan);
            library.ActiveBaseline = after; store.Write(library);
            CompleteManualAction(after);
            switches.Clear(after); switchConfiguration = null;
        }
        catch {
            // A topology mutation may have completed even when the call failed.
            // Do not interpret its return as a new, reverse switch on the next poll.
            switches.Reset(); switchConfiguration = previousConfiguration = null;
            screenNoticePending = false;
            throw;
        }
        return State();
    }
    public object UndoPreview()
    {
        var saved = store.Read().Undo ?? throw new InvalidOperationException("没有可撤销的恢复。");
        var current = Capture();
        return new { saved, plan = BuildOperation(saved, current, true), current, desktopKey = ContentKey(current) };
    }
    public object PreviewWithState(string id, string? targetId = null, bool allowResize = false)
    {
        var current = Capture();
        var saved = Find(id);
        return new { saved, plan = targetId is not null ? TransferPlanner.Build(saved, current, targetId, allowResize) : Build(saved, current), current, desktopKey = ContentKey(current) };
    }
    static RestorePlan Build(Snapshot saved, Snapshot current)
    {
        if (saved.IsSingleScreen) return SingleScreenLayouts.Build(saved, current);
        // Disconnected screens that never held template icons do not prevent
        // returning the whole saved arrangement to its still-connected source.
        var sourceIds = saved.Icons.Select(i => i.MonitorId).Distinct().ToList();
        if (saved.MonitorSet != current.MonitorSet && sourceIds.Count == 1
            && current.Displays.Any(d => d.Id == sourceIds[0]) && SingleScreenLayouts.CanCreate(saved))
            return SingleScreenLayouts.Build(SingleScreenLayouts.Create(saved), current);
        return Planner.Build(saved, current);
    }
    RestorePlan BuildOperation(Snapshot saved, Snapshot current, bool undo, string? targetId = null, bool allowResize = false)
    {
        if (!undo) return targetId is not null ? TransferPlanner.Build(saved, current, targetId, allowResize) : Build(saved, current);
        var basis=WithSavedGrid(saved,current);
        var scope=store.Read().UndoIconIds;
        RestorePlan plan;
        if (scope is null) plan=Build(saved,basis);
        else {
            var ids=scope.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshot=saved with { Icons=saved.Icons.Where(i=>ids.Contains(i.Id)).ToList() };
            var present=current.Icons.Select(i=>i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets=snapshot.Icons.Where(i=>present.Contains(i.Id)).Select(i=>i.MonitorId).ToHashSet();
            plan=saved.IsSingleScreen && scope.Count>0 ? SingleScreenLayouts.Build(snapshot,basis) : Planner.BuildScoped(snapshot,basis,targets);
        }
        return plan with { CurrentKey=current.ConfigKey, GridAlignment=plan.GridAlignment ?? (saved.FolderFlags & 4)!=0 };
    }
    void CompleteManualAction(Snapshot current)
    {
        changes.Accept(ContentKey(current));
        screenNoticePending = false;
        var library = store.Read();
        var defaultId = library.Records.Any(s => s.Id == library.DefaultSingleId && s.IsSingleScreen) ? library.DefaultSingleId : null;
        singleRecovery.Complete(current, defaultId);
        singleRecoveryMessage = null; singleRecoveryFailed = false;
    }
    Snapshot VerifyPlacement(IDesktopSession desktop, Snapshot before, RestorePlan plan)
    {
        // Large icon sets can settle later than one fixed 180ms sample.
        bool matched = false;
        for (int attempt = 0; attempt < 12; attempt++) {
            runtime.Wait(attempt == 0 ? 180 : 100);
            var after = desktop.Capture();
            if (after.ConfigKey != before.ConfigKey)
                throw new InvalidOperationException("恢复时屏幕配置发生变化。已保留恢复前备份，请检查桌面。");
            bool currentMatch = Verifier.Matches(before, plan, after);
            if (matched && currentMatch) return after;
            matched = currentMatch;
        }
        throw new InvalidOperationException("Windows 未按计划放置全部图标。已保留恢复前备份，可点击撤销。");
    }
    static bool SameIconIds(Snapshot a, Snapshot b) => a.Icons.Count == b.Icons.Count
        && a.Icons.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Icons.Select(i => i.Id));
    static Snapshot WithSavedGrid(Snapshot saved, Snapshot current) => SingleScreenLayouts.WithSavedGrid(saved, current);
    Snapshot Find(string id)
    {
        if (switches.Source?.Id == id) return switches.Source;
        var library = store.Read();
        return library.Current.Concat(library.History).SingleOrDefault(s => s.Id == id)
            ?? throw new InvalidOperationException("未找到该布局记录。");
    }
    public static bool HasChanged(Snapshot baseline, Snapshot current) => !Fingerprint.SameConfiguration(baseline, current) || ContentKey(baseline) != ContentKey(current);
    public static string ContentKey(Snapshot s) => Fingerprint.Of(s.Icons.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
        .Select(i => new { i.Id, i.MonitorId, i.X, i.Y }));
}
