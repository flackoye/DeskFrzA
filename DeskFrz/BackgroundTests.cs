using System.Text.Json;

namespace DeskFrz;

internal static class BackgroundTests
{
    public static void Run(Action<bool,string> assert)
    {
        var legacy = JsonSerializer.Deserialize<UiSettings>("{\"PanelGap\":64,\"CheckUpdates\":false}")!;
        assert(!legacy.BackgroundEnabled && legacy.PanelGap==64 && !legacy.CheckUpdates, "old settings without a resident flag remain manual-only");
        var tracker = new DesktopChangeTracker();
        var now = DateTimeOffset.UtcNow;
        assert(!tracker.Observe("original",now), "monitoring starts with a baseline instead of an immediate notification");
        assert(!tracker.Observe("dragging",now) && !tracker.Observe("settled",now.AddMilliseconds(500)), "intermediate drag positions are debounced");
        assert(tracker.Observe("settled",now.AddSeconds(2)) && !tracker.Observe("settled",now.AddSeconds(5)), "one settled layout produces one reminder");
        tracker.Observe("another",now.AddSeconds(6));
        assert(!tracker.Observe("another",now.AddSeconds(8)) && tracker.Observe("another",now.AddSeconds(13)), "rapid edits coalesce until the cooldown expires");
        tracker.Accept("another");
        assert(!tracker.IsDirty && !tracker.Observe("another",now.AddSeconds(15)), "save and restore acknowledge notification state");
        tracker.Accept(null);
        assert(!tracker.Observe("fresh",now.AddSeconds(16)) && !tracker.IsDirty, "re-enabling does not carry a prior session reminder");
        assert(WindowsNotifications.TryReadArguments("live:Desktop", "live", out var kind) && kind == ReviewKind.Desktop,
            "notification activation carries the desktop review type");
        assert(WindowsNotifications.TryReadArguments("live:Screen", "live", out kind) && kind == ReviewKind.Screen
            && WindowsNotifications.TryReadArguments("live:Recovery", "live", out kind) && kind == ReviewKind.Recovery,
            "screen and recovery notification clicks remain distinct");
        assert(WindowsNotifications.TryReadArguments("live:Test", "live", out kind) && kind is null,
            "test notifications open the window without inventing a desktop review");
        assert(!WindowsNotifications.TryReadArguments("old:Desktop", "live", out _)
            && !WindowsNotifications.TryReadArguments("live:0", "live", out _)
            && !WindowsNotifications.TryReadArguments("live:Unknown", "live", out _),
            "stale and invalid notification activations are ignored");
        assert(WindowsNotifications.AppId(Path.Combine(Path.GetTempPath(), "DeskFrz-test")) ==
            WindowsNotifications.AppId(Path.Combine(Path.GetTempPath(), "deskfrz-TEST") + Path.DirectorySeparatorChar),
            "Windows notification identity normalizes path case and trailing separators");
        assert(WindowsNotifications.AppId(Path.Combine(Path.GetTempPath(), "data-a")) !=
            WindowsNotifications.AppId(Path.Combine(Path.GetTempPath(), "data-b")),
            "separate layout libraries have separate Windows notification identities");
    }
}
