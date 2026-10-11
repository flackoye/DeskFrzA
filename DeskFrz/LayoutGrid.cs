namespace DeskFrz;

internal static class LayoutGrid
{
    // Explorer can report spacing in a different DPI basis from the current primary.
    // Calibrate only against an observed, aligned two-dimensional icon lattice.
    static int SpacingScale(Snapshot snapshot, int fallback)
    {
        if ((snapshot.FolderFlags & 4) == 0) return fallback;
        int Gcd(int a, int b) { while (b != 0) { (a, b) = (b, a % b); } return a; }
        int Step(IEnumerable<int> values) {
            var sorted = values.Distinct().Order().ToArray();
            return sorted.Zip(sorted.Skip(1), (a, b) => b - a).Aggregate(0, Gcd);
        }
        foreach (var screen in snapshot.Displays.OrderByDescending(m => snapshot.Icons.Count(i => i.MonitorId == m.Id)))
        {
            var icons = snapshot.Icons.Where(i => i.MonitorId == screen.Id).ToList();
            if (icons.Count < 5) continue;
            int x = Step(icons.Select(i => i.X)), y = Step(icons.Select(i => i.Y));
            if (x < 16 || y < 16) continue;
            double sx = snapshot.Spacing.X * (double)screen.Scale / x;
            double sy = snapshot.Spacing.Y * (double)screen.Scale / y;
            int scale = (int)Math.Round((sx + sy) / 2);
            if (scale >= 100 && scale <= 500 && Math.Abs(sx - sy) <= 1
                && Math.Abs(Math.Round(snapshot.Spacing.X * (double)screen.Scale / scale) - x) <= 1
                && Math.Abs(Math.Round(snapshot.Spacing.Y * (double)screen.Scale / scale) - y) <= 1) return scale;
        }
        return fallback;
    }
    public static Position Cell(Snapshot snapshot, Display screen, int primaryScale) {
        int basis = SpacingScale(snapshot, primaryScale);
        return new(Math.Max(16, (int)Math.Round(snapshot.Spacing.X * (double)screen.Scale / basis)),
            Math.Max(16, (int)Math.Round(snapshot.Spacing.Y * (double)screen.Scale / basis)));
    }
    public static Position Inset(Snapshot snapshot, Display screen, int primaryScale, Position cell) => new(
        Math.Max(0, (cell.X - (int)Math.Round(snapshot.IconSize * (double)screen.Scale / primaryScale)) / 2),
        Math.Max(1, (int)Math.Round(2.0 * screen.Scale / primaryScale)));
    public static bool Fits(Position p, Area area, Position cell, Position inset) => area.Contains(p)
        && p.X + cell.X - inset.X <= area.Right && p.Y + cell.Y - inset.Y <= area.Bottom;
    public static bool Collides(Position a, Position b, Position cell) =>
        Math.Abs(a.X - b.X) < cell.X && Math.Abs(a.Y - b.Y) < cell.Y;
    public static long Distance(Position a, Position b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
    public static IEnumerable<Position> Candidates(Area area, Position cell, Position inset)
    {
        // Explorer's conventional order is columns, then rows. Keep the same ordering on reflow.
        for (int x = area.X + inset.X; x + cell.X - inset.X <= area.Right; x += cell.X)
            for (int y = area.Y + inset.Y; y + cell.Y - inset.Y <= area.Bottom; y += cell.Y)
                yield return new Position(x, y);
    }
}
