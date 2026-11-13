namespace DeskFrz;

internal static class LayoutGrid
{
    // Icon gaps are multiples of a grid, not evidence of its fundamental spacing.
    // Use the captured view DPI so adding/removing icons cannot change the grid.
    public static Position Cell(Snapshot snapshot, Display screen, int primaryScale) {
        double basis = snapshot.SpacingDpi is int dpi ? dpi * 100.0 / 96 : primaryScale;
        return new(Math.Max(16, (int)Math.Round(snapshot.Spacing.X * (double)screen.Scale / basis)),
            Math.Max(16, (int)Math.Round(snapshot.Spacing.Y * (double)screen.Scale / basis)));
    }
    public static Position Inset(Snapshot snapshot, Display screen, int primaryScale, Position cell) => new(
        Math.Max(0, (cell.X - (int)Math.Round(snapshot.IconSize * screen.Scale / 100.0)) / 2),
        2);
    public static bool IsAligned(Snapshot snapshot, Display screen, Position position)
    {
        int scale = snapshot.Displays.Single(d => d.Primary).Scale;
        var cell = Cell(snapshot, screen, scale);
        var inset = Inset(snapshot, screen, scale, cell);
        return (position.X - screen.WorkArea.X - inset.X) % cell.X == 0
            && (position.Y - screen.WorkArea.Y - inset.Y) % cell.Y == 0;
    }
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
