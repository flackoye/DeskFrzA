using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskFrz;

public record Position(int X, int Y);
public record Area(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(Position p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
}
public record Display(string Id, string Name, string Device, bool Primary, Area Bounds,
    Area WorkArea, int Scale, uint Rotation, int TargetCount);
public record DesktopIcon(string Id, string Name, string MonitorId, int X, int Y);
public record Snapshot(string Id, DateTimeOffset SavedAt, string ConfigKey,
    List<Display> Displays, List<DesktopIcon> Icons, Position Spacing, Position ViewOrigin,
    int IconSize, uint FolderFlags)
{
    public string MonitorSet => string.Join('|', Displays.Select(m => m.Id).Order(StringComparer.Ordinal));
    public int Number { get; init; }
    public string Name { get; init; } = "";
    // DPI of the Shell view that reported Spacing. Older records have no basis;
    // their primary display scale is used without inferring from icon samples.
    public int? SpacingDpi { get; init; }
    public bool IsSingleScreen => Displays.Count == 1;
}
public record Move(string Id, string Name, string MonitorId, int X, int Y, bool IsNew);
public record RestorePlan(string SnapshotId, string CurrentKey, bool Exact, List<Move> Moves,
    int MissingCount, int NewCount, string? BlockedReason)
{
    public TransferReport? Transfer { get; init; }
    public bool? GridAlignment { get; init; }
}
public record TransferReport(string TargetId, int IconCount, double ScaleFactor, bool FreePlacement = false);
public record UiSettings(int PanelGap = 48, bool CheckUpdates = true)
{
    public bool BackgroundEnabled { get; init; }
}
public sealed class Library
{
    public int SchemaVersion { get; set; } = 1;
    public List<Snapshot> Current { get; set; } = [];
    public List<Snapshot> History { get; set; } = [];
    public Snapshot? Undo { get; set; }
    public UiSettings Settings { get; set; } = new();
    public int NextRecordNumber { get; set; } = 1;
    public string? ActiveId { get; set; }
    public bool SelectionInitialized { get; set; }
    public Snapshot? ActiveBaseline { get; set; }
    public string? UndoActiveId { get; set; }
    public Snapshot? UndoActiveBaseline { get; set; }
    public List<string>? UndoIconIds { get; set; }
    public string? DefaultSingleId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<Snapshot> Records => Current.Concat(History);
}
public static class Fingerprint
{
    public static string Of<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))))[..24];
    public static string Configuration(List<Display> displays, Position spacing, int iconSize, uint flags, int? spacingDpi = null)
    {
        var geometry = new { Displays = displays.OrderBy(m => m.Id).Select(m => new { m.Id, m.Primary,
            m.Bounds, m.WorkArea, m.Scale, m.Rotation, m.TargetCount }), spacing, iconSize,
            SnapToGrid = (flags & 4) != 0 };
        return spacingDpi is null ? Of(geometry) : Of(new { geometry, spacingDpi });
    }
    // Record equivalence supports older snapshots. Native race checks must still
    // compare the complete current ConfigKey, including captured view DPI.
    public static bool SameConfiguration(Snapshot a, Snapshot b) => a.ConfigKey == b.ConfigKey
        || ((a.SpacingDpi is null || b.SpacingDpi is null)
            && Configuration(a.Displays, a.Spacing, a.IconSize, a.FolderFlags)
                == Configuration(b.Displays, b.Spacing, b.IconSize, b.FolderFlags));
}
