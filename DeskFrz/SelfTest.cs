namespace DeskFrz;

public static class SelfTest
{
    public static object Run(string directory)
    {
        int count = 0;
        void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; }
        Display Screen(string id, int x, int width = 500, int scale = 100, bool primary = false) =>
            new(id, id, id, primary, new Area(x, 0, width, 500), new Area(x, 0, width, 500), scale, 1, 1);
        Snapshot Make(List<Display> screens, List<DesktopIcon> icons, uint flags = 0) => new(
            Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
            Fingerprint.Configuration(screens, new Position(100, 100), 48, flags), screens, icons,
            new Position(100, 100), new Position(-500, 0), 48, flags);
        var monitors = new List<Display> { Screen("left", -500), Screen("right", 0, primary: true) };
        var icons = new List<DesktopIcon> { new("a", "a", "left", -492, 8), new("b", "b", "right", 8, 8) };
        var saved = Make(monitors, icons);
        var moved = Make(monitors, [icons[0] with { X = -292 }, icons[1] with { Y = 208 }]);
        var plan = Planner.Build(saved, moved);
        Assert(plan.BlockedReason is null && plan.Exact && plan.Moves[0].X == -492, "exact negative-coordinate restore");
        var swapped = Make([Screen("left", 0, primary: true), Screen("right", 500)],
            [icons[0] with { X = 8 }, icons[1] with { X = 508 }]);
        plan = Planner.Build(saved, swapped);
        Assert(plan.BlockedReason is null && plan.Moves.Single(m => m.Id == "a").X == 8
            && plan.Moves.Single(m => m.Id == "b").X == 508, "follow monitor through primary switch and rearrangement");
        var enlarged = Make([Screen("left", -750, 750, 150), Screen("right", 0, primary: true)], moved.Icons);
        plan = Planner.Build(saved, enlarged);
        Assert(plan.BlockedReason is null && plan.Moves[0].MonitorId == "left", "mixed DPI and resized-monitor reflow");
        var spaced = Make(monitors, [new DesktopIcon("c", "c", "left", -274, 202)]);
        var scaled = Make([Screen("left", -1000, 1000, 150), monitors[1]], spaced.Icons);
        var gridPlan = Planner.Build(spaced, scaled);
        Assert(gridPlan.BlockedReason is null && gridPlan.Moves.Single().X == -661
            && gridPlan.Moves.Single().Y == 303, "preserve grid row and column through scale changes");
        var added = Make(monitors, [..moved.Icons, new DesktopIcon("new", "new", "left", -492, 8)]);
        plan = Planner.Build(saved, added);
        Assert(plan.NewCount == 1 && plan.Moves.Single(m => m.Id == "new").MonitorId == "left"
            && (plan.Moves.Single(m => m.Id == "new").X != -492 || plan.Moves.Single(m => m.Id == "new").Y != 8),
            "displace conflicting newcomer on same monitor");
        var clearNew = Make(monitors, [..moved.Icons, new DesktopIcon("new", "new", "left", -292, 208)]);
        Assert(Planner.Build(saved, clearNew).Moves.All(m => m.Id != "new"), "preserve non-conflicting newcomer");
        var expectedAfter = Make(monitors, [..saved.Icons, clearNew.Icons.Last()]);
        Assert(Verifier.Matches(clearNew, Planner.Build(saved, clearNew), expectedAfter), "verify moved icons and retained newcomers together");
        var unexpectedShift = expectedAfter with { Icons = [..saved.Icons, clearNew.Icons.Last() with { X = -192 }] };
        Assert(!Verifier.Matches(clearNew, Planner.Build(saved, clearNew), unexpectedShift), "reject unplanned newcomer displacement");
        Assert(Planner.Build(saved, Make(monitors, [icons[0]])).MissingCount == 1, "skip deleted icons");
        Assert(Planner.Build(saved, Make([monitors[0]], [icons[0]])).BlockedReason is not null, "refuse disconnected monitor set");
        var tiny = Make([Screen("left", -80, 80), monitors[1]], moved.Icons);
        Assert(Planner.Build(saved, tiny).BlockedReason is not null && Planner.Build(saved, tiny).Moves.Count == 0, "capacity preflight is all-or-nothing");
        Assert(Planner.Build(saved, Make(monitors, moved.Icons, 1)).BlockedReason is not null, "auto-arrange gate");
        Assert(Planner.Build(saved, Make([monitors[0] with { TargetCount = 2 }, monitors[1]], moved.Icons)).BlockedReason is not null, "clone-mode gate");
        var store = new Store(Path.Combine(directory, "selftest-" + Guid.NewGuid().ToString("N")));
        try
        {
            store.Save(saved); store.Save(moved);
            var library = store.Read();
            Assert(library.Current.Count == 1 && library.History.Single().Id == saved.Id, "one current record and preserved history");
            library.Undo = saved; store.Write(library);
            Assert(store.Read().Undo?.Id == saved.Id, "durable undo snapshot");
            var entries = store.Read().Records.OrderBy(s => s.Number).ToList();
            Assert(entries.Select(s => s.Number).SequenceEqual(new[] { 1, 2 }) && store.Read().ActiveId == moved.Id,
                "stable numbered records and saved selection");
            store.Rename(saved.Id, "办公桌面");
            Assert(store.Read().Records.Single(s => s.Id == saved.Id).Name == "办公桌面", "rename historical record persists");
            store.Delete(saved.Id);
            Assert(store.Read().Records.Single().Number == 1 && store.Read().ActiveId == moved.Id,
                "deletion renumbers selected layout without changing its identity");
            store.Save(saved with { Id = Guid.NewGuid().ToString("N"), SavedAt = DateTimeOffset.Now });
            Assert(store.Read().Records.Select(s => s.Number).Order().SequenceEqual(new[] { 1, 2 }), "continuous numbers after deletion and save");
            Assert(!DesktopService.HasChanged(saved, saved with { Id = "fresh-capture", SavedAt = DateTimeOffset.Now })
                && DesktopService.HasChanged(saved, moved), "detect position changes independent of capture identity");
            Assert(DesktopService.HasChanged(saved, added), "detect added desktop icons");
            var active = store.Read().ActiveId!; store.Delete(active);
            Assert(store.Read().ActiveId is null && store.Read().ActiveBaseline is null, "deleting selected record clears selection without desktop mutation");
            var legacy = new Library { Current = [moved], History = [saved] }; store.Write(legacy);
            var migrated = store.Read(); store.Write(migrated);
            Assert(store.Read().Records.All(s => s.Number > 0) && store.Read().Records.Count() == 2,
                "legacy current and history preserve all records during numbering");
            store.Write(new Library()); store.Save(saved); store.Save(moved);
            var third = moved with { Id = Guid.NewGuid().ToString("N"), SavedAt = DateTimeOffset.Now.AddSeconds(1) };
            store.Save(third); store.Delete(moved.Id);
            var remaining = store.Read();
            Assert(remaining.Records.OrderBy(s => s.Number).Select(s => s.Id).SequenceEqual(new[] { saved.Id, third.Id })
                && remaining.Records.Single(s => s.Id == third.Id).Number == 2 && remaining.ActiveId == third.Id,
                "delete middle record renumbers all entries while preserving active selection");
        }
        finally { System.IO.Directory.Delete(store.DirectoryPath, true); }
        // Regression: Explorer spacing 174x216 while the 100% primary uses 116x144.
        var mixedScreens = new List<Display> {
            new("laptop", "laptop", "DISPLAY1", false, new(-2560, 0, 2560, 1600), new(-2560, 0, 2560, 1552), 150, 1, 1),
            new("msi", "msi", "DISPLAY2", true, new(0, 0, 2560, 1440), new(0, 0, 2560, 1392), 100, 1, 1) };
        var mixedIcons = Enumerable.Range(0, 13).Select(n => new DesktopIcon("l" + n, "l" + n, "laptop", -2509 + n % 7 * 174, 2 + n / 7 * 216))
            .Concat(Enumerable.Range(0, 48).Select(n => new DesktopIcon("m" + n, "m" + n, "msi", 34 + n % 12 * 116, 2 + n / 12 * 144))).ToList();
        Snapshot Mixed(List<DesktopIcon> items) => new(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
            Fingerprint.Configuration(mixedScreens, new(174, 216), 48, 4), mixedScreens, items, new(174, 216), new(-2560, 0), 48, 4);
        var originalMixed = Mixed(mixedIcons);
        var gathered = Mixed(mixedIcons.Select((i, n) => i with { MonitorId = "msi", X = 34 + n % 12 * 116, Y = 2 + n / 12 * 144 }).ToList());
        var mixedRestore = Planner.Build(originalMixed, gathered);
        Assert(mixedRestore.BlockedReason is null && mixedRestore.Moves.Count == 61
            && mixedRestore.Moves.All(m => originalMixed.Icons.Any(i => i.Id == m.Id && i.X == m.X && i.Y == m.Y)), "mixed DPI exact restore keeps all 61 physical coordinates");
        Assert(LayoutGrid.Cell(gathered, mixedScreens[1], 100) == new Position(116, 144), "calibrate spacing from populated primary lattice");
        Assert(LayoutGrid.Cell(gathered, mixedScreens[0], 100) == new Position(174, 216), "calibrate empty secondary spacing without double scaling");
        var edge = originalMixed with { Icons = [new("edge", "edge", "msi", 2550, 1380)] };
        Assert(Planner.Build(edge, edge).BlockedReason is null, "exact restore does not reject captured edge position using estimated cell");
        var trackerAgain = new ScreenSwitchTracker(); trackerAgain.Observe(gathered);
        var changedPrimary = gathered with { Id = "switched", Displays = [mixedScreens[0] with { Primary = true }, mixedScreens[1] with { Primary = false }] };
        trackerAgain.Observe(changedPrimary);
        Assert(trackerAgain.Source?.Id == gathered.Id, "switch uses latest unsaved single-screen desktop");
        trackerAgain.Observe(gathered with { Id = "returned" });
        Assert(trackerAgain.Source?.Id == changedPrimary.Id, "repeat switch to same configuration retains new event identity");
        // The user's real single-screen layout, stripped of names and file identities.
        Position[] capturedPoints = [new Position(34, 578),
            new Position(34, 722),
            new Position(150, 578),
            new Position(150, 722),
            new Position(266, 578),
            new Position(266, 722),
            new Position(382, 578),
            new Position(382, 722),
            new Position(498, 578),
            new Position(498, 722),
            new Position(614, 578),
            new Position(614, 722),
            new Position(730, 722),
            new Position(34, 2),
            new Position(34, 146),
            new Position(34, 290),
            new Position(34, 434),
            new Position(34, 1154),
            new Position(34, 1298),
            new Position(266, 2),
            new Position(150, 146),
            new Position(150, 290),
            new Position(150, 434),
            new Position(150, 1154),
            new Position(150, 1298),
            new Position(382, 2),
            new Position(266, 146),
            new Position(382, 290),
            new Position(266, 434),
            new Position(266, 1154),
            new Position(266, 1298),
            new Position(614, 2),
            new Position(382, 146),
            new Position(498, 290),
            new Position(498, 1154),
            new Position(382, 1298),
            new Position(498, 2),
            new Position(730, 146),
            new Position(266, 290),
            new Position(614, 1154),
            new Position(498, 1298),
            new Position(150, 2),
            new Position(498, 146),
            new Position(962, 1298),
            new Position(1310, 2),
            new Position(962, 146),
            new Position(382, 1154),
            new Position(1078, 2),
            new Position(614, 146),
            new Position(846, 1298),
            new Position(730, 2),
            new Position(846, 146),
            new Position(1194, 1298),
            new Position(1194, 2),
            new Position(1078, 146),
            new Position(730, 1298),
            new Position(846, 2),
            new Position(1078, 1298),
            new Position(614, 1298),
            new Position(962, 2),
            new Position(1310, 1298)];
        var realSource = Mixed(capturedPoints.Select((q, n) => new DesktopIcon("r" + n, "r" + n, "msi", q.X, q.Y)).ToList());
        var realTargetScreens = new List<Display> { mixedScreens[0] with { Primary = true, Bounds = new(0, 0, 2560, 1600), WorkArea = new(0, 0, 2560, 1600) },
            mixedScreens[1] with { Primary = false, Bounds = new(2560, 0, 2560, 1440), WorkArea = new(2560, 0, 2560, 1440) } };
        var realCurrent = realSource with { Id = "current-switch", Displays = realTargetScreens, Spacing = new(169, 216),
            Icons = realSource.Icons.Select(i => i with { X = i.X + 2560 }).ToList(),
            ConfigKey = Fingerprint.Configuration(realTargetScreens, new(169, 216), 48, 4) };
        var actualTransfer = TransferPlanner.Build(realSource, realCurrent, "laptop");
        Assert(actualTransfer.BlockedReason is null && actualTransfer.Moves.Count == 61 && actualTransfer.Transfer?.FreePlacement == true,
            "real 61-icon 100-to-150 DPI switch offers confirmed free placement");
        Assert(actualTransfer.Moves.Select((m, n) => m.X - actualTransfer.Moves[0].X == capturedPoints[n].X - capturedPoints[0].X
            && m.Y - actualTransfer.Moves[0].Y == capturedPoints[n].Y - capturedPoints[0].Y).All(v => v), "real migration preserves every pair displacement without empty grid insertion");
        var largeGlyph = realCurrent with { IconSize = 128 };
        Assert(TransferPlanner.Build(realSource, largeGlyph, "laptop").BlockedReason is not null, "free placement still rejects actual enlarged icon collision");
        Position[] laptopPoints = [new Position(386, 1082),
            new Position(2414, 2),
            new Position(555, 1082),
            new Position(2414, 218),
            new Position(2076, 1298),
            new Position(2414, 434),
            new Position(1400, 218),
            new Position(2414, 650),
            new Position(217, 218),
            new Position(2414, 866),
            new Position(1062, 218),
            new Position(2414, 1082),
            new Position(2414, 1298),
            new Position(48, 2),
            new Position(48, 218),
            new Position(48, 434),
            new Position(48, 650),
            new Position(48, 1082),
            new Position(48, 1298),
            new Position(217, 2),
            new Position(386, 218),
            new Position(217, 434),
            new Position(217, 650),
            new Position(217, 1082),
            new Position(217, 1298),
            new Position(386, 2),
            new Position(555, 218),
            new Position(386, 434),
            new Position(386, 650),
            new Position(724, 1082),
            new Position(893, 1298),
            new Position(555, 2),
            new Position(724, 218),
            new Position(555, 434),
            new Position(1062, 1082),
            new Position(386, 1298),
            new Position(724, 2),
            new Position(893, 218),
            new Position(724, 434),
            new Position(1231, 1082),
            new Position(555, 1298),
            new Position(893, 2),
            new Position(1231, 218),
            new Position(724, 1298),
            new Position(1062, 2),
            new Position(1569, 218),
            new Position(893, 1082),
            new Position(1231, 2),
            new Position(1738, 218),
            new Position(1062, 1298),
            new Position(1400, 2),
            new Position(1907, 218),
            new Position(1231, 1298),
            new Position(1569, 2),
            new Position(2076, 218),
            new Position(1400, 1298),
            new Position(1738, 2),
            new Position(1569, 1298),
            new Position(1738, 1298),
            new Position(1907, 2),
            new Position(1907, 1298)];
        var laptopRecord = realCurrent with { Id = "laptop-record", Icons = laptopPoints.Select((q, n) => new DesktopIcon("r" + n, "r" + n, "laptop", q.X, q.Y)).ToList() };
        var fullMsiWork = gathered with { Icons = realSource.Icons,
            Displays = [mixedScreens[0], mixedScreens[1] with { WorkArea = mixedScreens[1].Bounds }] };
        var toMsi = TransferPlanner.Build(laptopRecord, fullMsiWork, "msi");
        Assert(toMsi.BlockedReason is null && toMsi.Transfer?.FreePlacement == true && toMsi.Moves.Count == 61,
            "169px laptop layout moving to 116px MSI grid requires explicit free placement");
        Assert(toMsi.Moves.Select((m, n) => m.X - toMsi.Moves[0].X == laptopPoints[n].X - laptopPoints[0].X
            && m.Y - toMsi.Moves[0].Y == laptopPoints[n].Y - laptopPoints[0].Y).All(v => v), "reverse DPI migration preserves all source gaps exactly");
        var gridAligned = realSource with { Icons = realSource.Icons.Take(12).ToList() };
        Assert(TransferPlanner.Build(gridAligned, gathered with { Icons = gridAligned.Icons }, "msi").Transfer?.FreePlacement == false,
            "already aligned coordinates do not disable grid unnecessarily");
        var postMigration = realCurrent with { FolderFlags = realCurrent.FolderFlags & ~4u,
            Icons = originalMixed.Icons.Select(i => i with { MonitorId = "laptop", X = 48, Y = 2 }).ToList() };
        var backToDual = Planner.Build(originalMixed, postMigration);
        Assert(backToDual.BlockedReason is null && backToDual.Moves.Count == 61
            && backToDual.Moves.All(m => {
                var oldIcon = originalMixed.Icons.Single(i => i.Id == m.Id);
                var oldMonitor = originalMixed.Displays.Single(d => d.Id == oldIcon.MonitorId);
                var newMonitor = postMigration.Displays.Single(d => d.Id == m.MonitorId);
                return m.X - newMonitor.Bounds.X == oldIcon.X - oldMonitor.Bounds.X
                    && m.Y - newMonitor.Bounds.Y == oldIcon.Y - oldMonitor.Bounds.Y;
            }), "grid disabled and primary spacing changes never reflow the dual-screen history");
        var onMsiAfterMigration = originalMixed with { FolderFlags = originalMixed.FolderFlags & ~4u,
            Icons = originalMixed.Icons.Select(i => i with { MonitorId = "msi", X = 48, Y = 2 }).ToList() };
        var msiBackToDual = Planner.Build(originalMixed, onMsiAfterMigration);
        Assert(msiBackToDual.BlockedReason is null && msiBackToDual.Moves.Count == 61
            && msiBackToDual.Moves.All(m => originalMixed.Icons.Any(i => i.Id == m.Id && i.X == m.X && i.Y == m.Y)),
            "MSI primary migration followed by dual restore preserves every saved physical coordinate");
        SingleScreenTests.Run(directory, Assert);
        FeatureTests.Transfer(Assert);
        FeatureTests.Updates(directory, Assert).GetAwaiter().GetResult();
        return new { passed = count };
    }
}
