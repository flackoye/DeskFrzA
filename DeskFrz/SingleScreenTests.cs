namespace DeskFrz;

internal static class SingleScreenTests
{
    public static void Run(string directory, Action<bool, string> assert)
    {
        Display Screen(string id, int x, bool primary = true, int scale = 100, int width = 1000) =>
            new(id, id, id, primary, new(x, 0, width, 800), new(x, 0, width, 760), scale, 1, 1);
        Snapshot Scene(List<Display> screens, List<DesktopIcon> icons, uint flags = 0) =>
            new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                Fingerprint.Configuration(screens, new(100, 100), 48, flags), screens, icons, new(100, 100), new(0, 0), 48, flags);
        bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var laptop = Screen("laptop", -1000, false, 150);
        var external = Screen("external", 0);
        var dual = Scene([laptop, external], [new("a", "a", "laptop", -970, 10), new("b", "b", "laptop", -820, 10)]);
        var single = SingleScreenLayouts.Create(dual);
        assert(single.IsSingleScreen && single.Displays.Single().Primary && single.Displays.Single().Id == "laptop"
            && single.Icons.SequenceEqual(dual.Icons), "single template retains all source coordinates from a dual-screen record");
        assert(single.Spacing == new Position(150, 150) && dual.Displays.Count == 2, "single template calibrates source spacing without modifying original");
        assert(Reject(() => SingleScreenLayouts.Create(dual with { Icons = [dual.Icons[0], dual.Icons[1] with { MonitorId = "external" }] })), "cannot save a multi-screen icon distribution as single");
        assert(Reject(() => SingleScreenLayouts.Create(dual with { Displays = [laptop, external with { TargetCount = 2 }] })), "clone mode cannot initialize single template");
        var unplugged = Scene([laptop with { Primary = true, Bounds = new(0, 0, 1000, 800), WorkArea = new(0, 0, 1000, 760) }],
            dual.Icons.Select((i, n) => i with { X = 300 + n * 150, Y = 300 }).ToList());
        var plan = SingleScreenLayouts.Build(single, unplugged);
        assert(plan.BlockedReason is null && plan.Moves.Select(m => new Position(m.X, m.Y)).SequenceEqual([new Position(30, 10), new Position(180, 10)]), "unplug recovery translates saved laptop coordinates from former negative origin");
        assert(plan.Moves[1].X - plan.Moves[0].X == 150, "unplug recovery preserves neighboring distances");
        var projected = unplugged with { Icons = plan.Moves.Select(m => new DesktopIcon(m.Id, m.Name, m.MonitorId, m.X, m.Y)).ToList() };
        assert(Verifier.Matches(unplugged, plan, projected), "single recovery verification accepts only planned positions");
        var another = Scene([Screen("another", 0, scale: 150)], unplugged.Icons.Select(i => i with { MonitorId = "another" }).ToList());
        var otherPlan = SingleScreenLayouts.Build(single, another);
        assert(otherPlan.BlockedReason is null && otherPlan.Moves.All(m => m.MonitorId == "another"), "single default supports remaining external screen or changed device identity");
        var smaller = Scene([Screen("small", 0, width: 16)], another.Icons.Select(i => i with { MonitorId = "small" }).ToList());
        var crowded = SingleScreenLayouts.Build(single, smaller);
        assert(crowded.BlockedReason is not null && crowded.Moves.Count == 0, "capacity failure is atomic and never produces a partial single recovery");
        assert(SingleScreenLayouts.Build(single, unplugged with { FolderFlags = 1 }).BlockedReason is not null, "automatic recovery respects auto-arrange guard");
        var changed = unplugged with { Icons = [unplugged.Icons[0], new("new", "new", "laptop", 30, 10)] };
        var changedPlan = SingleScreenLayouts.Build(single, changed);
        assert(changedPlan.BlockedReason is null && changedPlan.MissingCount == 1 && changedPlan.NewCount == 1
            && new Position(changedPlan.Moves.Single(m => m.IsNew).X, changedPlan.Moves.Single(m => m.IsNew).Y) != new Position(30, 10), "deleted icons are skipped and conflicting new icon gets a free position");
        var empty = Scene([Screen("empty", 0)], []);
        assert(SingleScreenLayouts.Create(empty).IsSingleScreen && SingleScreenLayouts.Build(empty, empty).BlockedReason is null, "empty desktop can initialize and recover");
        var gate = new SingleScreenRecovery(); var now = DateTimeOffset.UtcNow;
        assert(!gate.Ready(unplugged, null, now) && !gate.Pending, "no automatic restore before mandatory default setup");
        assert(!gate.Ready(unplugged, single.Id, now) && gate.Pending
            && !gate.Ready(unplugged, single.Id, now.AddSeconds(1)), "startup waits for single-screen configuration to settle");
        assert(gate.Ready(unplugged, single.Id, now.AddSeconds(2)) && !gate.Pending
            && !gate.Ready(unplugged, single.Id, now.AddSeconds(10)), "startup recovery and failures are latched once, not repeated");
        assert(!gate.Ready(unplugged with { ConfigKey = "grid-changed", FolderFlags = 4 }, single.Id, now.AddSeconds(11)), "grid or icon settings do not cause a repeated automatic restore");
        gate.Ready(dual, single.Id, now.AddSeconds(12));
        assert(!gate.Ready(unplugged, single.Id, now.AddSeconds(13)) && gate.Ready(unplugged, single.Id, now.AddSeconds(15)), "reconnecting then unplugging arms a new recovery");
        gate.Retry();
        assert(!gate.Ready(unplugged, single.Id, now.AddSeconds(16)) && gate.Ready(unplugged, single.Id, now.AddSeconds(18)), "explicit retry waits for stable screen before recovering");
        var store = new Store(Path.Combine(directory, "single-" + Guid.NewGuid().ToString("N")));
        try {
            store.Save(dual);
            store.Save(single with { Id = "single-one" }, false);
            store.SetDefaultSingle("single-one");
            assert(store.Read().Records.All(s => s.Number == 1), "single and multi groups each start numbering at one");
            assert(store.Read().ActiveId == dual.Id && store.Read().DefaultSingleId == "single-one", "default setup preserves active multi-screen selection and persists default");
            assert(Reject(() => store.Delete("single-one")) && store.Read().Records.Count() == 2, "default single layout cannot be deleted");
            assert(Reject(() => store.SetDefaultSingle(dual.Id)) && store.Read().DefaultSingleId == "single-one", "multi-screen record cannot become default without explicit template creation");
            store.Save(single with { Id = "single-two", SavedAt = now.AddSeconds(1) }, false);
            store.SetDefaultSingle("single-two"); store.Delete("single-one");
            assert(store.Read().Records.All(s => s.Number == 1) && store.Read().ActiveId == dual.Id
                && store.Read().DefaultSingleId == "single-two", "deletion renumbers only its group without changing active or default IDs");
            assert(store.Read().DefaultSingleId == "single-two" && store.Read().Records.Count() == 2
                && store.Read().Records.Single(s => s.Id == dual.Id).Displays.Count == 2, "one default can be replaced and old single deleted without altering dual history");
        }
        finally { System.IO.Directory.Delete(store.DirectoryPath, true); }
    }
}
