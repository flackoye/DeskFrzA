namespace DeskFrz;

/// <summary>Single-screen templates retain local coordinates, independently of other connected displays.</summary>
public static class SingleScreenLayouts
{
    public static bool CanCreate(Snapshot snapshot) => snapshot.Displays.All(d => d.TargetCount == 1)
        && snapshot.Icons.Select(i => i.MonitorId).Distinct().Count() <= 1;

    public static Snapshot Create(Snapshot snapshot)
    {
        if (!CanCreate(snapshot)) throw new InvalidOperationException("请先将图标放到一块屏幕，再保存单屏布局。");
        if (snapshot.IsSingleScreen) return snapshot;
        var source = snapshot.Displays.Single(d => d.Id == (snapshot.Icons.FirstOrDefault()?.MonitorId
            ?? snapshot.Displays.Single(d => d.Primary).Id));
        var cell = LayoutGrid.Cell(snapshot, source, snapshot.Displays.Single(d => d.Primary).Scale);
        var displays = new List<Display> { source with { Primary = true } };
        int? dpi = snapshot.SpacingDpi is null ? null : (int)Math.Round(source.Scale * 96.0 / 100);
        return snapshot with { Displays = displays, Spacing = cell,
            SpacingDpi = dpi, ConfigKey = Fingerprint.Configuration(displays, cell, snapshot.IconSize, snapshot.FolderFlags, dpi) };
    }

    internal static Snapshot WithSavedGrid(Snapshot saved, Snapshot current) {
        uint flags = (current.FolderFlags & ~4u) | (saved.FolderFlags & 4);
        return current with { FolderFlags = flags,
            ConfigKey = Fingerprint.Configuration(current.Displays, current.Spacing, current.IconSize, flags, current.SpacingDpi) };
    }

    public static RestorePlan Build(Snapshot saved, Snapshot current)
    {
        if (!saved.IsSingleScreen) throw new InvalidOperationException("请选择单屏布局。");
        var originalKey = current.ConfigKey;
        var exact = Fingerprint.SameConfiguration(saved, current);
        if (current.Displays.Count == 1) current = WithSavedGrid(saved, current);
        var source = saved.Displays.Single();
        var target = current.Displays.SingleOrDefault(d => d.Id == source.Id)
            ?? current.Displays.Single(d => d.Primary);
        // Only target-screen newcomers participate. Matched icons can come back from
        // any screen; unrelated icons on other screens are outside this template.
        var ids = saved.Icons.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scoped = current with { Icons = current.Icons.Where(i => i.MonitorId == target.Id || ids.Contains(i.Id)).ToList() };
        var template = saved with { Displays = [source with { Id = target.Id, Primary = true }],
            Icons = saved.Icons.Select(i => i with { MonitorId = target.Id }).ToList() };
        // Keep the original primary DPI as the spacing basis even when the target is secondary.
        var plan = Planner.Build(template, scoped, target.Id);
        bool? grid = current.Displays.Count == 1 ? plan.GridAlignment ?? (saved.FolderFlags & 4) != 0 : plan.GridAlignment;
        return plan with { Exact = exact, CurrentKey = originalKey, GridAlignment = grid };
    }
}

/// <summary>Recover once per stable single-screen configuration, including app startup.</summary>
internal sealed class SingleScreenRecovery
{
    string? candidate, handled, settling;
    DateTimeOffset since;
    public bool Pending => candidate is not null && handled != candidate;
    public bool Ready(Snapshot current, string? defaultId, DateTimeOffset now)
    {
        if (current.Displays.Count != 1 || defaultId is null) { candidate = handled = settling = null; return false; }
        var key = Key(current, defaultId);
        // Latch by screen configuration, but wait for icons and Shell settings too.
        // Windows can continue moving icons after the display list has settled.
        var desktop = current.ConfigKey + ":" + DesktopService.ContentKey(current);
        if (candidate != key) { candidate = key; settling = desktop; since = now; return false; }
        if (handled == key) return false;
        if (settling != desktop) { settling = desktop; since = now; return false; }
        if (now - since < TimeSpan.FromSeconds(2)) return false;
        handled = key; // Latch failures too. Retry is explicit, never a repeating desktop mutation.
        return true;
    }
    static string Key(Snapshot current, string defaultId) => defaultId + ":" + Fingerprint.Of(current.Displays);
    public void Complete(Snapshot current, string? defaultId)
    {
        candidate = handled = current.Displays.Count == 1 && defaultId is not null ? Key(current, defaultId) : null;
    }
    public void Retry() { candidate = handled = settling = null; }
}
