using static DeskFrz.LayoutGrid;
namespace DeskFrz;
/// <summary>One screen's layout is one rigid block. Never reflow individual saved icons.</summary>
public static class TransferPlanner
{
    public static RestorePlan Build(Snapshot saved, Snapshot current, string targetId, bool allowResize = false)
    {
        RestorePlan Block(string reason) => new(saved.Id, current.ConfigKey, false, [], 0, 0, reason);
        var sourceIds = saved.Icons.Select(i => i.MonitorId).Distinct().ToList();
        if (sourceIds.Count != 1) return Block("图标分布在多个屏幕，不建议切换主屏幕。");
        var source = saved.Displays.SingleOrDefault(s => s.Id == sourceIds[0]);
        var target = current.Displays.SingleOrDefault(s => s.Id == targetId);
        if (source is null || target is null) return Block("屏幕已变化，请重新预览。");
        if (current.Displays.Any(s => s.TargetCount != 1)) return Block("复制显示模式不支持迁移。");
        if ((current.FolderFlags & 1) != 0) return Block("请先关闭桌面的自动排列。");
        var now = current.Icons.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var original = saved.Icons.Where(i => now.ContainsKey(i.Id)).ToList();
        if (original.Count == 0) return Block("记录中的图标已不存在。");
        var oldIds = saved.Icons.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newcomers = current.Icons.Where(i => !oldIds.Contains(i.Id)).ToList();
        var cell = Cell(current, target, current.Displays.Single(s => s.Primary).Scale);
        var sourceCell = Cell(saved, source, saved.Displays.Single(s => s.Primary).Scale);
        bool freePlacement = (current.FolderFlags & 4) != 0 && (saved.FolderFlags & 4) != 0
            && (cell.X > sourceCell.X || cell.Y > sourceCell.Y);
        if (freePlacement) {
            // Grid padding is not an icon collision. Keep the original occupied cell,
            // bounded below by the target's icon glyph; do not insert new empty cells.
            int glyph = (int)Math.Ceiling(current.IconSize * target.Scale / 100.0);
            cell = new(Math.Max(glyph, Math.Min(cell.X, sourceCell.X)),
                Math.Max(glyph, Math.Min(cell.Y, sourceCell.Y)));
        }
        var inset = Inset(current, target, current.Displays.Single(s => s.Primary).Scale, cell);
        int minX = original.Min(i => i.X), minY = original.Min(i => i.Y);
        int spanX = original.Max(i => i.X) - minX, spanY = original.Max(i => i.Y) - minY;
        int availableX = target.WorkArea.Width - cell.X, availableY = target.WorkArea.Height - cell.Y;
        double factor = Math.Min(1, Math.Min(spanX == 0 ? 1 : (double)availableX / spanX,
            spanY == 0 ? 1 : (double)availableY / spanY));
        var report = new TransferReport(target.Id, original.Count, factor, freePlacement);
        RestorePlan Finish(List<Move> moves, string? reason) => new(saved.Id, current.ConfigKey, false, moves,
            saved.Icons.Count - original.Count, newcomers.Count, reason) { Transfer = report };
        if (factor <= 0) return Finish([], "屏幕空间不足，请缩小桌面图标后重试。");
        // A single scale applies to both axes; no DPI/grid conversion may stretch the block.
        int width = (int)Math.Round(spanX * factor), height = (int)Math.Round(spanY * factor);
        int left = target.WorkArea.X + inset.X, top = target.WorkArea.Y + inset.Y;
        int right = target.WorkArea.Right - cell.X + inset.X - width;
        int bottom = target.WorkArea.Bottom - cell.Y + inset.Y - height;
        if (right < left || bottom < top) return Finish([], "屏幕空间不足，请缩小桌面图标后重试。");
        int anchorX = Math.Clamp(target.WorkArea.X + minX - source.WorkArea.X, left, right);
        int anchorY = Math.Clamp(target.WorkArea.Y + minY - source.WorkArea.Y, top, bottom);
        var positions = original.Select(i => new Position(anchorX + (int)Math.Round((i.X - minX) * factor),
            anchorY + (int)Math.Round((i.Y - minY) * factor))).ToList();
        var targetGrid = Cell(current, target, current.Displays.Single(s => s.Primary).Scale);
        var targetInset = Inset(current, target, current.Displays.Single(s => s.Primary).Scale, targetGrid);
        // Even a smaller target grid can distort the block: 169px steps cannot
        // land exactly on a 116px grid. Consent is based on the planned positions.
        freePlacement |= (current.FolderFlags & 4) != 0 && positions.Any(p =>
            (p.X - target.WorkArea.X - targetInset.X) % targetGrid.X != 0
            || (p.Y - target.WorkArea.Y - targetInset.Y) % targetGrid.Y != 0);
        report = report with { FreePlacement = freePlacement };
        for (int i = 0; i < positions.Count; i++)
        {
            if (!Fits(positions[i], target.WorkArea, cell, inset)
                || positions.Take(i).Any(p => Collides(p, positions[i], cell)))
                return Finish([], "无法保持布局且避免重叠，请缩小桌面图标后重试。");
        }
        if (newcomers.Where(i => i.MonitorId == target.Id)
            .Any(i => positions.Any(p => Collides(p, new Position(i.X, i.Y), cell))))
            return Finish([], "新增图标占用了迁移位置，请先移开这些图标。");
        if (factor < 1 && !allowResize) return Finish([], "需要整体等比例缩小布局。");
        return Finish(original.Select((i, index) => new Move(i.Id, now[i.Id].Name, target.Id,
            positions[index].X, positions[index].Y, false)).ToList(), null);
    }
}
