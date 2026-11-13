using System.Text.Json;

namespace DeskFrz;

/// <summary>Exercise the real service against an in-memory Windows boundary.</summary>
internal static class RestoreTests
{
    static Display Screen(string id, int x = 0, bool primary = true, int width = 1000, int scale = 100) =>
        new(id, id, id, primary, new(x, 0, width, 1000), new(x, 0, width, 1000), scale, 1, 1);
    static Snapshot Scene(List<Display> displays, List<DesktopIcon> icons, uint flags = 0, int spacing = 100, int? dpi = null) =>
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            Fingerprint.Configuration(displays, new(spacing, spacing), 48, flags, dpi), displays, icons,
            new(spacing, spacing), new(0, 0), 48, flags) { SpacingDpi = dpi };
    static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static bool Changed(object value) => Data(value).GetProperty("changed").GetBoolean();
    static bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    static void Restore(DesktopService service, MemoryRuntime runtime, Snapshot saved, bool undo = false, bool allowFree = false) =>
        service.Restore(saved.Id, runtime.Scene.ConfigKey, DesktopService.ContentKey(runtime.Scene), undo, allowFreePlacement: allowFree);

    internal static IDesktopRuntime CreateUiFixture(Store store, bool backgroundEnabled = false)
    {
        if (Directory.Exists(store.DirectoryPath) && Directory.EnumerateFileSystemEntries(store.DirectoryPath).Any())
            throw new InvalidOperationException("界面自检需要一个新的空数据目录。");
        var displays = new List<Display> { Screen("a", -1800, false, 1800), Screen("b", width:1800) };
        var icons = Enumerable.Range(0,70).Select(n=>new DesktopIcon("i"+n,"测试图标 "+n,"b",26+n%10*100,2+n/10*100)).ToList();
        var current=Scene(displays,icons,4,100,96) with { Name="当前现场 70 图标" };
        var fifty=Scene(displays,icons.Take(50).Select(i=>i with {MonitorId="a",X=i.X-1800}).ToList(),4,100,96) with {Name="双屏历史 50 图标"};
        var free=Scene([displays[0] with {Primary=true}],fifty.Icons.Select(i=>i with {X=i.X+4,Y=i.Y+8}).ToList(),0,100,96) with {Name="单屏自由摆放"};
        store.Save(current); store.Save(fifty,false); store.Save(free,false); store.SetDefaultSingle(free.Id);
        var library=store.Read(); library.Settings=library.Settings with {CheckUpdates=false,BackgroundEnabled=backgroundEnabled}; store.Write(library);
        return new MemoryRuntime(current);
    }

    public static void Run(string directory, Action<bool, string> assert)
    {
        string root = Path.Combine(directory, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Store Library(string name) => new(Path.Combine(root, name));
        void EnableBackground(Store target) {
            var library=target.Read(); library.Settings=library.Settings with {BackgroundEnabled=true}; target.Write(library);
        }
        var a = Screen("a", width: 2000); var b = Screen("b", 2000, false);
        try {
            var icons = Enumerable.Range(0, 70).Select(n => new DesktopIcon("i" + n, "i" + n, "a", 26 + n % 10 * 100, 2 + n / 10 * 100)).ToList();
            var seventy = Scene([a, b], icons);
            var fifty = Scene([a, b], icons.Take(50).ToList());
            var store = Library("counts"); store.Save(seventy); store.Save(fifty);
            var runtime = new MemoryRuntime(seventy);
            var service = new DesktopService(store, runtime);
            foreach (bool conflict in new[] { false, true }) {
                runtime.Scene = seventy with { Icons = icons.Select((i, n) => conflict
                    ? n < 50 ? i with { Y = i.Y + 400 } : i with { X = 26 + (n - 50) % 10 * 100, Y = 2 + (n - 50) / 10 * 100 }
                    : i with { X = i.X + 100 }).ToList() };
                for (int n = 0; n < 4; n++) {
                    var target = n % 2 == 0 ? fifty : seventy;
                    var beforeIcons = runtime.Scene.Icons.ToDictionary(i => i.Id);
                    Restore(service, runtime, target);
                    assert(runtime.Scene.Icons.Count == 70 && !Changed(service.State()), $"70/50 restore retains all icons and baseline (conflict={conflict}, step={n})");
                    var actual = runtime.Scene.Icons.ToDictionary(i => i.Id);
                    assert(target.Icons.All(i => actual[i.Id] == i),
                        $"every historical icon returns to its saved position (conflict={conflict}, step={n})");
                    assert(actual.Values.Select((icon,index) => actual.Values.Take(index).All(other =>
                        icon.MonitorId != other.MonitorId || !LayoutGrid.Collides(new(icon.X,icon.Y),new(other.X,other.Y),seventy.Spacing))).All(x=>x),
                        $"all seventy restored and extra icons have separate positions (conflict={conflict}, step={n})");
                    if (!conflict && n==0)
                        assert(icons.Skip(50).All(i=>actual[i.Id]==beforeIcons[i.Id]), "twenty non-conflicting extras retain their exact current positions");
                }
            }
            assert(store.Read().Records.Single(s => s.Id == fifty.Id).Icons.Count == 50, "restoring never overwrites historical icon count");
            runtime.Scene = fifty;
            Restore(service, runtime, seventy);
            assert(runtime.Scene.Icons.Count == 50 && !Changed(service.State()), "deleted twenty are skipped and actual fifty form the baseline");
            runtime.Scene = runtime.Scene with { Icons = [..runtime.Scene.Icons, new("extra", "extra", "a", 1526, 802)] };
            assert(Changed(service.State()), "adding an icon after restore is a real change");
            Restore(service, runtime, fifty);
            runtime.Scene = runtime.Scene with { Icons = runtime.Scene.Icons.Select(i => i.Id == "extra" ? i with { X = 1626 } : i).ToList() };
            assert(Changed(service.State()), "moving an extra icon after restore is not swallowed by template matching");

            var sa = Screen("a", width: 500) with { Bounds = new(0, 0, 500, 500), WorkArea = new(0, 0, 500, 500) };
            var sb = Screen("b", 500, false, 500) with { Bounds = new(500, 0, 500, 500), WorkArea = new(500, 0, 500, 500) };
            var single = Scene([sa], [new("x", "x", "a", 26, 2)]);
            var dual = Scene([sa, sb], [new("x", "x", "b", 626, 2), new("other", "other", "b", 990, 490)]);
            var singlePlan = SingleScreenLayouts.Build(single, dual);
            assert(singlePlan.BlockedReason is null && singlePlan.Moves.Single().MonitorId == "a", "single template returns matched icon from another screen without touching unrelated icons");
            var narrow = sb with { Bounds = new(500, 0, 16, 500), WorkArea = new(500, 0, 16, 500) };
            var narrowScene = dual with { Displays = [sa, narrow], Icons = [new("x", "x", "a", 126, 2), new("other", "other", "b", 500, 2)] };
            assert(SingleScreenLayouts.Build(single, narrowScene).BlockedReason is null, "unrelated screen capacity cannot block a single template");
            var singleStore = Library("single"); singleStore.Save(single);
            runtime = new(dual); service = new(singleStore, runtime); Restore(service, runtime, single);
            assert(runtime.Scene.Icons.Single(i => i.Id == "other") == dual.Icons.Single(i => i.Id == "other"), "single template leaves unrelated screen positions unchanged through execution");
            var undoScopeStore=Library("single-undo-scope");
            var scopeBefore=Scene([sa,sb],[new("x","x","a",26,2),new("other","other","b",526,2)]);
            var scopeTarget=Scene([sa],[new("x","x","a",226,2)]);
            undoScopeStore.Save(scopeTarget);
            runtime=new(scopeBefore); service=new(undoScopeStore,runtime); Restore(service,runtime,scopeTarget);
            runtime.Scene=runtime.Scene with {Icons=[runtime.Scene.Icons[0],runtime.Scene.Icons[1] with {X=626},new("new-b","new-b","b",526,2)]};
            var scopedUndo=Data(service.UndoPreview()).GetProperty("plan");
            assert(scopedUndo.GetProperty("moves").GetArrayLength()==1 && scopedUndo.GetProperty("moves")[0].GetProperty("id").GetString()=="x",
                "undo preview of a single-screen action excludes independent later changes on another screen");
            Restore(service,runtime,scopeTarget,undo:true);
            assert(runtime.Scene.Icons.Single(i=>i.Id=="x").X==26 && runtime.Scene.Icons.Single(i=>i.Id=="other").X==626
                && runtime.Scene.Icons.Single(i=>i.Id=="new-b").X==526,
                "undo reverses the action without rearranging unrelated-screen additions or user moves");

            var noOpStore=Library("no-op-undo"); noOpStore.Save(single);
            runtime=new(single); service=new(noOpStore,runtime); Restore(service,runtime,single);
            runtime.Scene=runtime.Scene with {Icons=[single.Icons[0] with {X=126}]};
            Restore(service,runtime,single,undo:true);
            assert(runtime.Scene.Icons[0].X==126, "undo of a no-op does not roll back a later manual icon movement");

            var emptySecondary=Scene([sa,sb],single.Icons);
            var connectedSourceStore=Library("empty-secondary"); connectedSourceStore.Save(emptySecondary);
            runtime=new(Scene([sa],[single.Icons[0] with {X=126}])); service=new(connectedSourceStore,runtime);
            assert(Data(service.PreviewWithState(emptySecondary.Id)).GetProperty("plan").GetProperty("blockedReason").ValueKind==JsonValueKind.Null,
                "disconnecting an empty historical secondary does not block the connected source layout");
            Restore(service,runtime,emptySecondary);
            assert(runtime.Scene.Icons[0].X==26 && !Changed(service.State()), "multi-screen record concentrated on the surviving screen restores without a transfer");
            var split=emptySecondary with {Id=Guid.NewGuid().ToString("N"),Icons=[..emptySecondary.Icons,new("lost-screen","lost-screen","b",526,2)]};
            connectedSourceStore.Save(split,false);
            assert(Data(service.PreviewWithState(split.Id)).GetProperty("plan").GetProperty("blockedReason").ValueKind==JsonValueKind.String,
                "records that actually contain disconnected-screen icons remain blocked");
            var changingGrid=Scene([sa],[single.Icons[0] with {X=63}],4,174,96);
            var alignedHistorical=Scene([sa,sb],single.Icons,4,100,96);
            var changedGridStore=Library("changed-grid"); changedGridStore.Save(alignedHistorical);
            runtime=new(changingGrid); service=new(changedGridStore,runtime);
            var changedGridPlan=Data(service.PreviewWithState(alignedHistorical.Id)).GetProperty("plan");
            assert(changedGridPlan.GetProperty("gridAlignment").GetBoolean() && changedGridPlan.GetProperty("moves")[0].GetProperty("x").GetInt32()==63,
                "surviving-screen history adapts to changed grid spacing instead of requesting off-grid coordinates with alignment enabled");
            Restore(service,runtime,alignedHistorical);
            assert(runtime.Scene.FolderFlags==4 && runtime.Scene.Icons[0].X==63, "changed grid spacing stays aligned through actual service execution");

            var free = Scene([sa], [new("x", "x", "a", 30, 10)]);
            var alignedDual = Scene([sa, sb], [new("x", "x", "a", 126, 2), new("other", "other", "b", 990, 490)], 4);
            var gridStore = Library("grid"); gridStore.Save(free);
            runtime = new(alignedDual); service = new(gridStore, runtime);
            var preview = Data(service.PreviewWithState(free.Id));
            assert(!preview.GetProperty("plan").GetProperty("gridAlignment").GetBoolean(), "dual-screen free placement is declared in the preview");
            assert(Reject(() => Restore(service, runtime, free)) && runtime.Scene.FolderFlags == 4 && gridStore.Read().Undo is null,
                "free placement requires consent before backup or grid mutation");
            Restore(service, runtime, free, allowFree: true);
            assert(runtime.Scene.FolderFlags == 0 && runtime.Scene.Icons.Single(i => i.Id == "x").X == 30
                && runtime.Scene.Icons.Single(i => i.Id == "other").X == 990, "consented free placement preserves target and unrelated screen coordinates");
            Restore(service, runtime, free, undo: true);
            assert(runtime.Scene.FolderFlags == 4 && gridStore.Read().Undo is null, "undo restores the prior grid and clears its backup");

            var autoFreeStore=Library("automatic-free-grid"); autoFreeStore.Save(free); autoFreeStore.SetDefaultSingle(free.Id);
            EnableBackground(autoFreeStore);
            runtime=new(Scene([sa],[new("x","x","a",126,2)],4)); service=new(autoFreeStore,runtime);
            service.Observe(); runtime.Wait(2500); service.Observe();
            assert(runtime.Scene.FolderFlags==0 && runtime.Scene.Icons[0].X==30 && !Data(service.State()).GetProperty("singleRecoveryFailed").GetBoolean(),
                "automatic single recovery restores an intentionally free default without requiring migration consent");

            var defaultLayout = Scene([sa], [new("x", "x", "a", 26, 2)]);
            var chosen = Scene([sa], [new("x", "x", "a", 226, 2)]);
            var modeStore = Library("background-mode"); modeStore.Save(defaultLayout); modeStore.SetDefaultSingle(defaultLayout.Id);
            runtime = new(chosen); service = new(modeStore, runtime);
            assert(!modeStore.Read().Settings.BackgroundEnabled, "legacy and new libraries default to manual-only mode");
            service.Observe(); runtime.Wait(3000); service.State();
            assert(runtime.ApplyCalls == 0 && runtime.Scene.Icons[0].X == 226, "manual-only State and Observe never auto-restore a default template");
            int modeEvents = 0; service.BackgroundModeChanged += _ => modeEvents++;
            service.Settings(56, false, true); service.Observe(); runtime.Wait(3000); service.Observe();
            assert(runtime.ApplyCalls == 0 && modeStore.Read().Settings.BackgroundEnabled, "enabling monitoring preserves the current desktop and persists the choice");
            service.Settings(64);
            assert(new Store(modeStore.DirectoryPath).Read().Settings.BackgroundEnabled && !modeStore.Read().Settings.CheckUpdates && modeEvents == 1,
                "partial settings updates and a new Store preserve the background choice without duplicate mode transitions");
            service = new(modeStore, runtime); service.Observe(); runtime.Wait(2500); service.Observe();
            assert(runtime.ApplyCalls == 1 && runtime.Scene.Icons[0].X == 26, "a persisted enabled mode arms single-screen recovery on the next process lifetime");
            runtime.Scene = Scene([sa,sb], defaultLayout.Icons); service.Observe();
            runtime.Scene = chosen; service.Observe(); service.Settings(64, backgroundEnabled:false);
            runtime.Wait(3000); service.Observe(); service.State();
            assert(runtime.ApplyCalls == 1 && !modeStore.Read().Settings.BackgroundEnabled && !Data(service.State()).GetProperty("singleRecoveryPending").GetBoolean(),
                "disabling monitoring cancels pending recovery and its queued state");

            var manualStore = Library("manual-without-default"); runtime = new(dual); service = new(manualStore,runtime);
            service.Save();
            assert(manualStore.Read().Records.Count() == 1 && manualStore.Read().DefaultSingleId is null,
                "a desktop library can save a multi-screen layout without mandatory single-screen setup");
            var optionalDefaultStore = Library("optional-default");
            var optionalRuntime = new MemoryRuntime(single); var optionalService = new DesktopService(optionalDefaultStore,optionalRuntime);
            optionalService.Save(true);
            assert(optionalDefaultStore.Read().DefaultSingleId is null,
                "saving a single-screen library record does not implicitly opt into default-layout protection");
            optionalService.DefaultSingle(optionalDefaultStore.Read().ActiveId); optionalRuntime.Wait(3000); optionalService.State();
            assert(optionalDefaultStore.Read().DefaultSingleId is not null && optionalRuntime.ApplyCalls == 0,
                "users can choose an optional default without enabling resident mode or automatic restoration");
            int notices = 0; service.DesktopChanged += () => notices++;
            service.Settings(48, backgroundEnabled:true);
            runtime.Scene = runtime.Scene with {Icons=runtime.Scene.Icons.Select(i=>i.Id=="x" ? i with {X=726} : i).ToList()};
            service.Observe(); runtime.Wait(1500); service.Observe(); service.State(); service.Observe();
            assert(notices == 1, "a stable desktop edit emits one native notification across timer and State observations");
            service.Save(); runtime.Wait(3000); service.Observe();
            assert(notices == 1 && !service.HasPendingReview, "saving acknowledges the current layout and clears pending notification state");
            runtime.Scene = runtime.Scene with {Icons=runtime.Scene.Icons.Select(i=>i.Id=="x" ? i with {X=826} : i).ToList()};
            service.Observe(); runtime.Wait(1500); service.Observe();
            assert(notices == 2, "a later independent edit can notify again after saving");
            Restore(service,runtime,manualStore.Read().Records.First()); runtime.Wait(2000); service.Observe();
            assert(notices == 2 && !service.HasPendingReview, "successful manual restoration does not manufacture a new edit notification");
            service.Settings(48, backgroundEnabled:false);
            runtime.Scene=runtime.Scene with {Icons=runtime.Scene.Icons.Select(i=>i.Id=="x" ? i with {X=926} : i).ToList()};
            service.Observe(); runtime.Wait(3000); service.State();
            assert(notices == 2, "manual-only mode emits no native edit notifications");

            var gateStore = Library("gate"); gateStore.Save(defaultLayout); gateStore.SetDefaultSingle(defaultLayout.Id); gateStore.Save(chosen, false);
            EnableBackground(gateStore);
            runtime = new(defaultLayout); service = new(gateStore, runtime);
            service.Observe(); Restore(service, runtime, chosen);
            var undoId = gateStore.Read().Undo!.Id;
            runtime.Wait(3000); service.Observe();
            assert(runtime.Scene.Icons[0].X == 226 && gateStore.Read().Undo?.Id == undoId, "successful manual restore consumes pending auto recovery without overwriting undo");
            service.DefaultSingle(chosen.Id); runtime.Wait(2500); service.Observe();
            assert(gateStore.Read().ActiveId == chosen.Id && runtime.ApplyCalls == 2, "changing the default arms one new recovery");
            runtime.Wait(3000); service.Observe();
            assert(runtime.ApplyCalls == 2, "automatic recovery does not repeat for a handled configuration");
            service.DefaultSingle(chosen.Id); runtime.Wait(3000); service.Observe();
            assert(runtime.ApplyCalls == 2, "setting the same default does not arm another automatic recovery");
            runtime.Scene = defaultLayout;
            service.RetrySingleRecovery(); runtime.ThrowOnApply = true; runtime.Wait(2500); service.Observe();
            assert(Data(service.State()).GetProperty("singleRecoveryFailed").GetBoolean(), "automatic failure is exposed and latched");
            int failures = runtime.ApplyCalls; runtime.Wait(3000); service.Observe();
            assert(runtime.ApplyCalls == failures, "failed automatic recovery never loops");
            runtime.ThrowOnApply = false; service.RetrySingleRecovery(); runtime.Wait(2500); service.Observe();
            assert(runtime.Scene.Icons[0].X == 226 && !Data(service.State()).GetProperty("singleRecoveryFailed").GetBoolean(), "explicit retry re-arms and clears failure after success");

            var saveStore = Library("save-gate"); saveStore.Save(defaultLayout); saveStore.SetDefaultSingle(defaultLayout.Id);
            EnableBackground(saveStore);
            runtime = new(chosen); service = new(saveStore, runtime); service.Observe(); service.Save(true);
            runtime.Wait(3000); service.Observe();
            assert(runtime.ApplyCalls == 0 && runtime.Scene.Icons[0].X == 226, "saving a manual single layout consumes pending default recovery");
            var setupStore=Library("initial-setup");
            runtime=new(emptySecondary); service=new(setupStore,runtime); service.DefaultSingle(null);
            assert(setupStore.Read().ActiveId==setupStore.Read().DefaultSingleId && !Changed(service.State()),
                "first default extracted from a dual desktop selects its template and keeps the complete live baseline");
            runtime.Scene=runtime.Scene with {Icons=[runtime.Scene.Icons[0] with {X=126}]};
            assert(Changed(service.State()), "manual movement after first dual-screen setup remains detectable");
            var earlyStore = Library("early-gate"); earlyStore.Save(defaultLayout); earlyStore.SetDefaultSingle(defaultLayout.Id); earlyStore.Save(chosen, false);
            EnableBackground(earlyStore);
            runtime = new(defaultLayout); service = new(earlyStore, runtime); Restore(service, runtime, chosen);
            runtime.Wait(3000); service.Observe();
            assert(runtime.ApplyCalls == 1 && runtime.Scene.Icons[0].X == 226, "manual restore before first observation also consumes automatic recovery");

            var selectionStore = Library("known"); selectionStore.Save(defaultLayout); selectionStore.Save(chosen);
            runtime = new(defaultLayout); service = new(selectionStore, runtime);
            assert(!Changed(service.State()) && selectionStore.Read().ActiveId == defaultLayout.Id, "returning exactly to an existing layout selects it without a new-record prompt");
            runtime.Scene = defaultLayout with { Icons = [..defaultLayout.Icons, new("extra", "extra", "a", 126, 102)] };
            assert(Changed(service.State()), "known-layout recognition requires the complete icon set");
            runtime.Scene = dual;
            assert(Changed(service.State()), "a single-screen template is not recognized as the entire dual desktop");
            var noSelectionStore=Library("known-without-selection"); noSelectionStore.Save(defaultLayout,false);
            runtime=new(chosen); service=new(noSelectionStore,runtime); service.State(); runtime.Scene=defaultLayout;
            assert(!Changed(service.State()) && noSelectionStore.Read().ActiveId==defaultLayout.Id,
                "an existing full layout can be recognized after initial selection found no match");

            var sparse = Scene([Screen("a", width: 2000, scale: 200)], [new("0", "0", "a", 52, 2), new("1", "1", "a", 452, 2), new("2", "2", "a", 852, 2), new("3", "3", "a", 52, 402), new("4", "4", "a", 452, 402)], 4, 200);
            var dense = Scene([a], [new("0", "0", "a", 26, 2), new("1", "1", "a", 126, 2), new("2", "2", "a", 226, 2), new("3", "3", "a", 26, 102), new("4", "4", "a", 126, 102)], 4);
            var sparsePlan = Planner.Build(sparse, dense);
            assert(sparsePlan.BlockedReason is null && sparsePlan.Moves.Single(m => m.Id == "1").X - sparsePlan.Moves.Single(m => m.Id == "0").X == 200,
                "DPI adaptation preserves intentionally empty grid columns");
            var calibrated = sparse with { SpacingDpi = 192 };
            assert(LayoutGrid.Cell(calibrated, calibrated.Displays[0], 200) == new Position(200, 200)
                && LayoutGrid.Cell(calibrated with { Icons = [] }, calibrated.Displays[0], 200) == new Position(200, 200), "captured grid basis does not depend on icon count or lattice density");
            var metadataStore = Library("metadata"); metadataStore.Save(calibrated); metadataStore.Save(defaultLayout);
            assert(metadataStore.Read().Records.Single(s => s.Id == calibrated.Id).SpacingDpi == 192
                && metadataStore.Read().Records.Single(s => s.Id == defaultLayout.Id).SpacingDpi is null, "new and legacy grid metadata round-trip together");
            var upgraded = defaultLayout with { Id = Guid.NewGuid().ToString("N"), SpacingDpi = 96,
                ConfigKey = Fingerprint.Configuration(defaultLayout.Displays, defaultLayout.Spacing, defaultLayout.IconSize, defaultLayout.FolderFlags, 96) };
            var upgradeStore = Library("upgrade"); upgradeStore.Save(defaultLayout);
            runtime = new(upgraded); service = new(upgradeStore, runtime);
            assert(!Changed(service.State()) && upgradeStore.Read().ActiveId == defaultLayout.Id, "new DPI metadata does not report an unchanged legacy desktop as modified");
            upgradeStore.Save(upgraded);
            assert(upgradeStore.Read().Current.Count == 1 && upgradeStore.Read().History.Single().Id == defaultLayout.Id,
                "saving a new capture archives the equivalent legacy current record");
            assert(DesktopService.HasChanged(defaultLayout, upgraded with { Icons = [upgraded.Icons[0] with { X = 126 }] }), "legacy compatibility still detects actual icon movement");
            var normalized = SingleScreenLayouts.Create(Scene([sa,sb],single.Icons, dpi:96));
            assert(normalized.SpacingDpi==96 && normalized.Displays.Count==1, "single-template extraction retains an explicit normalized spacing basis");
            assert(SingleScreenLayouts.Create(Scene([sa,sb],single.Icons)).SpacingDpi is null,
                "extracting a legacy template does not label inferred spacing as native DPI evidence");
            var oldMixed = Scene([Screen("a", width: 2000), Screen("b", 2000, false, scale:150)],
                [new("0","0","a",34,2),new("1","1","a",266,2),new("2","2","a",34,290)], 4, 174);
            var newMixed = Scene([Screen("a", width: 2000, scale:150), Screen("b", 2000, false, scale:150)],
                oldMixed.Icons, 4, 174,144);
            var oldMixedPlan=Planner.Build(oldMixed,newMixed);
            assert(oldMixedPlan.BlockedReason is null && oldMixedPlan.Moves.Single(m=>m.Id=="1").X-oldMixedPlan.Moves.Single(m=>m.Id=="0").X==348,
                "legacy mixed-DPI adaptation preserves a deliberate empty column without guessing the source cell");

            var switched = Scene([sa with { Primary = false, Bounds = new(500, 0, 500, 500), WorkArea = new(500, 0, 500, 500) },
                sb with { Primary = true, Bounds = new(0, 0, 500, 500), WorkArea = new(0, 0, 500, 500) }],
                [new("x", "x", "a", 626, 2), new("other", "other", "b", 26, 2)]);
            var source = Scene([sa, sb], [new("x", "x", "a", 26, 2), new("other", "other", "b", 526, 2)]);
            var topologyStore = Library("switch-topology"); topologyStore.Save(source); EnableBackground(topologyStore);
            runtime = new(source); service = new(topologyStore,runtime); service.Observe();
            runtime.Scene = switched; service.Observe();
            assert(service.HasPendingReview && Data(service.State()).GetProperty("canUndoSwitch").GetBoolean(),
                "a primary change creates a pending review with a valid switch undo");
            runtime.Scene = Scene([..switched.Displays, Screen("c",1000,false,500)], switched.Icons);
            service.Observe();
            var topologyState = Data(service.State());
            assert(!service.HasPendingReview && !topologyState.GetProperty("canUndoSwitch").GetBoolean()
                && topologyState.GetProperty("switchSource").ValueKind==JsonValueKind.Null,
                "adding a third screen without another primary change clears an obsolete review and native undo configuration");
            foreach (string failure in new[] { "none", "configuration", "apply", "placement", "settle" }) {
                var switchStore = Library("switch-" + failure); switchStore.Save(source);
                EnableBackground(switchStore);
                runtime = new(source); service = new(switchStore, runtime); service.Observe();
                runtime.Scene = switched; service.Observe();
                if (failure == "none") {
                    service.AcknowledgeReview(ReviewKind.Screen);
                    assert(!service.HasPendingReview && Data(service.State()).GetProperty("switchSource").ValueKind != JsonValueKind.Null,
                        "acknowledging a screen reminder clears its pending state without losing the switch undo source");
                }
                runtime.OnConfigurationRestore = () => {
                    assert(switchStore.Read().Undo is not null, "switch backup precedes native mutation (" + failure + ")");
                    if (failure == "configuration") throw new InvalidOperationException("simulated topology failure");
                    if (failure == "apply") runtime.ThrowOnApply = true;
                    if (failure == "placement") runtime.NeverPlace = true;
                    if (failure == "settle") runtime.Scene = runtime.Scene with { ConfigKey = "unsettled" };
                };
                if (failure == "none") {
                    service.UndoScreenSwitch(runtime.Scene.ConfigKey, DesktopService.ContentKey(runtime.Scene));
                    assert(runtime.ApplyCalls == 1 && runtime.Scene.Icons.SequenceEqual(source.Icons), "switch undo captures and applies with the same live session");
                } else {
                    assert(Reject(() => service.UndoScreenSwitch(runtime.Scene.ConfigKey, DesktopService.ContentKey(runtime.Scene))), "switch undo reports " + failure + " failure");
                    assert(switchStore.Read().Undo is not null && switchStore.Read().ActiveBaseline!.Id == source.Id, "failed switch undo retains backup and prior successful baseline");
                }
                assert(Data(service.State()).GetProperty("switchSource").ValueKind == JsonValueKind.Null, "switch undo never leaves a stale or reverse switch prompt (" + failure + ")");
            }

            var undoStore = Library("undo-preview"); undoStore.Save(chosen);
            var undoLibrary = undoStore.Read(); undoLibrary.Undo = defaultLayout; undoLibrary.UndoActiveId = chosen.Id; undoLibrary.UndoActiveBaseline = chosen; undoStore.Write(undoLibrary);
            runtime = new(dual); service = new(undoStore, runtime);
            var undoPreview = Data(service.UndoPreview()).GetProperty("plan");
            assert(undoPreview.GetProperty("blockedReason").ValueKind == JsonValueKind.Null, "single-screen undo preview supports the same current dual configuration as execution");
            Restore(service, runtime, chosen, undo: true);
            assert(Data(runtime.LastMoves).GetRawText() == undoPreview.GetProperty("moves").GetRawText(), "undo preview and execution share the same move plan");

            var unsaved = chosen with { Id=Guid.NewGuid().ToString("N"), Icons=[chosen.Icons[0] with { X=326 }] };
            var unsavedStore=Library("unsaved-undo"); unsavedStore.Save(chosen); unsavedStore.Save(defaultLayout,false);
            runtime=new(unsaved); service=new(unsavedStore,runtime); Restore(service,runtime,defaultLayout); Restore(service,runtime,defaultLayout,undo:true);
            assert(runtime.Scene.Icons[0].X==326 && Changed(service.State()), "undo preserves unsaved-change semantics from before restoration");

            var staleStore=Library("stale"); staleStore.Save(defaultLayout);
            runtime=new(chosen); service=new(staleStore,runtime);
            string currentKey=runtime.Scene.ConfigKey, desktopKey=DesktopService.ContentKey(runtime.Scene);
            runtime.Scene=runtime.Scene with {Icons=[chosen.Icons[0] with {X=326}]};
            assert(Reject(()=>service.Restore(defaultLayout.Id,currentKey,desktopKey)) && staleStore.Read().Undo is null && runtime.ApplyCalls==0,
                "stale preview never creates a backup or mutates the desktop");

            var settleChangeStore=Library("mid-capture"); settleChangeStore.Save(defaultLayout);
            runtime=new(chosen); service=new(settleChangeStore,runtime);
            runtime.OnCapture = number => { if(number==2) runtime.Scene=runtime.Scene with {Icons=[chosen.Icons[0] with {X=326}]}; };
            assert(Reject(()=>Restore(service,runtime,defaultLayout)) && runtime.ApplyCalls==0 && settleChangeStore.Read().Undo is not null,
                "changes between the preflight check and placement capture pause execution and retain backup");

            var ordinaryFree = Scene([sa,sb], [new("x","x","a",30,10),new("other","other","b",530,10)]);
            var ordinaryStore=Library("ordinary-grid"); ordinaryStore.Save(ordinaryFree);
            runtime=new(alignedDual); service=new(ordinaryStore,runtime);
            assert(Data(service.PreviewWithState(ordinaryFree.Id)).GetProperty("plan").GetProperty("gridAlignment").ValueKind==JsonValueKind.False,
                "ordinary multi-screen history also declares required free placement");
            assert(Reject(()=>Restore(service,runtime,ordinaryFree)) && runtime.Scene.FolderFlags==4,
                "ordinary multi-screen restoration cannot silently disable the grid");
            Restore(service,runtime,ordinaryFree,allowFree:true);
            assert(runtime.Scene.FolderFlags==0 && runtime.Scene.Icons[0].X==30 && runtime.Scene.Icons[1].X==530,
                "consented ordinary restoration keeps saved free coordinates on their original screens");

            var settleStore = Library("settle"); settleStore.Save(defaultLayout);
            runtime = new(chosen) { PlacementFrames = 3 }; service = new(settleStore, runtime); Restore(service, runtime, defaultLayout);
            assert(runtime.CapturesAfterApply == 5 && !Changed(service.State()), "delayed Shell placement is verified in two samples before publishing a successful baseline");
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class MemoryRuntime(Snapshot initial) : IDesktopRuntime
    {
        public Snapshot Scene = initial;
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public int ApplyCalls, CapturesAfterApply, PlacementFrames;
        public bool ThrowOnApply, NeverPlace;
        public Action? OnConfigurationRestore;
        public Action<int>? OnCapture;
        public int CaptureCount;
        public List<Move> LastMoves = [];
        Snapshot? pending;
        public IDisposable EnterDpiScope() => new Scope();
        public IDesktopSession Open() => new Session(this);
        public void Wait(int milliseconds) => Now += TimeSpan.FromMilliseconds(milliseconds);
        public IDisplayConfiguration CaptureConfiguration(List<Display> displays) => new Configuration(this, Scene);
        sealed class Scope : IDisposable { public void Dispose() { } }
        sealed class Session(MemoryRuntime runtime) : IDesktopSession
        {
            Snapshot? captured;
            bool applied;
            public Snapshot Capture() {
                runtime.OnCapture?.Invoke(++runtime.CaptureCount);
                if (applied) {
                    runtime.CapturesAfterApply++;
                    if (runtime.pending is not null && runtime.CapturesAfterApply > runtime.PlacementFrames) { runtime.Scene = runtime.pending; runtime.pending = null; }
                }
                return captured = runtime.Scene with { Id = Guid.NewGuid().ToString("N"), SavedAt = runtime.Now };
            }
            public void Apply(List<Move> moves, Position origin) {
                runtime.ApplyCalls++;
                if (captured is null || moves.Any(m => !captured.Icons.Any(i => i.Id == m.Id)) || origin != captured.ViewOrigin)
                    throw new InvalidOperationException("Apply requires this session's current capture and origin");
                if (runtime.ThrowOnApply) throw new InvalidOperationException("simulated Apply failure");
                runtime.LastMoves = moves; applied = true; runtime.CapturesAfterApply = 0;
                var after = runtime.Scene with { Icons = runtime.Scene.Icons.Select(i => {
                    var move = moves.SingleOrDefault(m => m.Id == i.Id);
                    return move is null ? i : i with { X = move.X, Y = move.Y, MonitorId = move.MonitorId };
                }).ToList() };
                if (!runtime.NeverPlace) runtime.pending = after;
            }
            public void SetGridAlignment(bool enabled) {
                uint flags = (runtime.Scene.FolderFlags & ~4u) | (enabled ? 4u : 0u);
                runtime.Scene = runtime.Scene with { FolderFlags = flags, ConfigKey = Fingerprint.Configuration(runtime.Scene.Displays, runtime.Scene.Spacing, runtime.Scene.IconSize, flags, runtime.Scene.SpacingDpi) };
            }
            public void DisableAutoArrange() { }
            public void Dispose() { }
        }
        sealed class Configuration(MemoryRuntime runtime, Snapshot source) : IDisplayConfiguration
        {
            public bool Supports(List<Display> current) => source.MonitorSet == string.Join('|', current.Select(d => d.Id).Order(StringComparer.Ordinal));
            public void Validate(List<Display> current) { if (!Supports(current)) throw new InvalidOperationException("different screens"); }
            public void Restore(List<Display> current) {
                Validate(current);
                runtime.Scene = runtime.Scene with { Displays = source.Displays, Spacing = source.Spacing, SpacingDpi = source.SpacingDpi, ConfigKey = source.ConfigKey };
                runtime.OnConfigurationRestore?.Invoke();
            }
        }
    }
}
