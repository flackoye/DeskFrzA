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
        return snapshot with { Displays = displays, Spacing = cell,
            ConfigKey = Fingerprint.Configuration(displays, cell, snapshot.IconSize, snapshot.FolderFlags) };
    }

    internal static Snapshot WithSavedGrid(Snapshot saved, Snapshot current) {
        uint flags = (current.FolderFlags & ~4u) | (saved.FolderFlags & 4);
        return current with { FolderFlags = flags,
            ConfigKey = Fingerprint.Configuration(current.Displays, current.Spacing, current.IconSize, flags) };
    }

    public static RestorePlan Build(Snapshot saved, Snapshot current)
    {
        if (!saved.IsSingleScreen) throw new InvalidOperationException("请选择单屏布局。");
        var originalKey = current.ConfigKey;
        if (current.Displays.Count == 1) current = WithSavedGrid(saved, current);
        var source = saved.Displays.Single();
        var target = current.Displays.SingleOrDefault(d => d.Id == source.Id)
            ?? current.Displays.Single(d => d.Primary);
        // Supply empty templates for other screens: their new icons stay on their own screen.
        var displays = current.Displays.Select(d => d.Id == target.Id
            ? source with { Id = target.Id, Primary = d.Primary } : d).ToList();
        var basis = displays.Single(d => d.Primary).Scale;
        var spacing = new Position((int)Math.Round(saved.Spacing.X * basis / (double)source.Scale),
            (int)Math.Round(saved.Spacing.Y * basis / (double)source.Scale));
        var template = saved with { Displays = displays, Spacing = spacing,
            Icons = saved.Icons.Select(i => i with { MonitorId = target.Id }).ToList() };
        return Planner.Build(template, current) with { Exact = saved.ConfigKey == originalKey, CurrentKey = originalKey };
    }
}

/// <summary>Recover once per stable single-screen configuration, including app startup.</summary>
internal sealed class SingleScreenRecovery
{
    string? candidate, handled;
    DateTimeOffset since;
    public bool Pending => candidate is not null && handled != candidate;
    public bool Ready(Snapshot current, string? defaultId, DateTimeOffset now)
    {
        if (current.Displays.Count != 1 || defaultId is null) { candidate = handled = null; return false; }
        var key = defaultId + ":" + Fingerprint.Of(current.Displays);
        if (candidate != key) { candidate = key; since = now; return false; }
        if (handled == key || now - since < TimeSpan.FromSeconds(2)) return false;
        handled = key; // Latch failures too. Retry is explicit, never a repeating desktop mutation.
        return true;
    }
    public void Retry() { candidate = handled = null; }
}
