using static DeskFrz.LayoutGrid;

namespace DeskFrz;

/// <summary>Pure preflight. Never changes the desktop; refuses partial/missing-monitor restores.</summary>
public static class Planner
{
    public static RestorePlan Build(Snapshot saved, Snapshot current) => BuildCore(saved, current, null);

    internal static RestorePlan Build(Snapshot saved, Snapshot current, string targetId) => BuildScoped(saved, current, new HashSet<string> {targetId});
    internal static RestorePlan BuildScoped(Snapshot saved, Snapshot current, IReadOnlySet<string> targets) => BuildCore(saved, current, targets);
    static RestorePlan BuildCore(Snapshot saved, Snapshot current, IReadOnlySet<string>? targets)
    {
        var exact = Fingerprint.SameConfiguration(saved, current);
        RestorePlan Block(string reason) => new(saved.Id, current.ConfigKey, exact, [], 0, 0, reason);
        if ((targets is null && saved.MonitorSet != current.MonitorSet)
            || (targets is not null && targets.Any(id=>!current.Displays.Any(d=>d.Id==id) || !saved.Displays.Any(d=>d.Id==id))))
            return Block("显示器组合不同。请连接原来的显示器后恢复。");
        if (current.Displays.Any(m => m.TargetCount != 1))
            return Block("当前是复制显示模式。请切换为扩展模式后恢复。");
        if ((current.FolderFlags & 1) != 0)
            return Block("请先关闭桌面的自动排列。");
        var now = current.Icons.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var oldIds = saved.Icons.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newcomers = current.Icons.Where(i => !oldIds.Contains(i.Id)).ToList();
        List<Move> moves = [];
        var primaryScale = current.Displays.Single(m => m.Primary).Scale;
        foreach (var screen in current.Displays.Where(s => targets is null || targets.Contains(s.Id)))
        {
            var before = saved.Displays.Single(m => m.Id == screen.Id);
            var cell = Cell(current, screen, primaryScale);
            var inset = Inset(current, screen, primaryScale, cell);
            var old = saved.Icons.Where(i => i.MonitorId == screen.Id && now.ContainsKey(i.Id))
                .OrderBy(i => i.X).ThenBy(i => i.Y).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
            var added = newcomers.Where(i => i.MonitorId == screen.Id).ToList();
            var occupied = new List<Position>();
            var candidates = Candidates(screen.WorkArea, cell, inset).ToList();
            // Shell grid state and reported spacing are not changes to the saved
            // physical layout. Preserve coordinates when the same screen can hold them.
            bool geometryChanged = before.Bounds.Width != screen.Bounds.Width || before.Bounds.Height != screen.Bounds.Height
                || before.Scale != screen.Scale || before.Rotation != screen.Rotation
                || saved.IconSize != current.IconSize
                || old.Any(i => !screen.WorkArea.Contains(new Position(
                    i.X - before.Bounds.X + screen.Bounds.X, i.Y - before.Bounds.Y + screen.Bounds.Y)));
            var savedPrimaryScale = saved.Displays.Single(m => m.Primary).Scale;
            var oldCell = Cell(saved, before, savedPrimaryScale);
            var oldInset = Inset(saved, before, savedPrimaryScale, oldCell);
            geometryChanged |= (saved.FolderFlags & current.FolderFlags & 4) != 0 && oldCell != cell
                && old.Any(i=>!IsAligned(current,screen,new(i.X-before.Bounds.X+screen.Bounds.X,i.Y-before.Bounds.Y+screen.Bounds.Y)));
            var preferred = old.Select(i => new Position(
                screen.WorkArea.X + inset.X + Math.Max(0, (int)Math.Round((i.X - before.WorkArea.X - oldInset.X) / (double)oldCell.X)) * cell.X,
                screen.WorkArea.Y + inset.Y + Math.Max(0, (int)Math.Round((i.Y - before.WorkArea.Y - oldInset.Y) / (double)oldCell.Y)) * cell.Y)).ToList();
            if (saved.SpacingDpi is null || (saved.FolderFlags & 4) == 0) {
                // A legacy snapshot has no proven source grid. Scale its physical
                // gaps first, then use only the current grid for aligned layouts.
                double ratio = screen.Scale / (double)before.Scale;
                preferred = old.Select(i => {
                    var p = new Position(screen.WorkArea.X + (int)Math.Round((i.X - before.WorkArea.X) * ratio),
                        screen.WorkArea.Y + 2 + (int)Math.Round((i.Y - before.WorkArea.Y - 2) * ratio));
                    if ((saved.FolderFlags & current.FolderFlags & 4) != 0)
                        p = new(screen.WorkArea.X + inset.X + Math.Max(0, (int)Math.Round((p.X - screen.WorkArea.X - inset.X) / (double)cell.X)) * cell.X,
                            screen.WorkArea.Y + inset.Y + Math.Max(0, (int)Math.Round((p.Y - screen.WorkArea.Y - inset.Y) / (double)cell.Y)) * cell.Y);
                    return p;
                }).ToList();
            }
            var keepGrid = preferred.All(p => Fits(p, screen.WorkArea, cell, inset))
                && preferred.Select((p, index) => preferred.Take(index).All(o => !Collides(o, p, cell))).All(valid => valid);
            int oldIndex = 0;
            foreach (var icon in old)
            {
                Position position;
                if (!geometryChanged)
                {
                    position = new Position(icon.X - before.Bounds.X + screen.Bounds.X,
                                            icon.Y - before.Bounds.Y + screen.Bounds.Y);
                    if (!screen.Bounds.Contains(position) || occupied.Contains(position))
                        return Block($"{screen.Name} 的原布局存在越界或重叠，恢复已暂停。");
                }
                else
                {
                    var free = keepGrid ? preferred[oldIndex] : candidates.FirstOrDefault(p => !occupied.Any(o => Collides(o, p, cell)));
                    if (free is null) return Block($"{screen.Name} 空间不足。请调整图标大小或分辨率后重试。");
                    position = free;
                }
                occupied.Add(position);
                moves.Add(new Move(icon.Id, now[icon.Id].Name, screen.Id, position.X, position.Y, false));
                oldIndex++;
            }
            // Reserve every non-conflicting new icon before moving conflicting new icons.
            List<DesktopIcon> displaced = [];
            foreach (var icon in added)
            {
                var p = new Position(icon.X, icon.Y);
                if (Fits(p, screen.WorkArea, cell, inset) && !occupied.Any(o => Collides(o, p, cell))) occupied.Add(p);
                else displaced.Add(icon);
            }
            foreach (var icon in displaced)
            {
                var free = candidates.Where(p => !occupied.Any(o => Collides(o, p, cell)))
                    .OrderBy(p => Distance(p, new Position(icon.X, icon.Y))).FirstOrDefault();
                if (free is null) return Block($"{screen.Name} 无法容纳全部图标，恢复已暂停。");
                occupied.Add(free);
                moves.Add(new Move(icon.Id, icon.Name, screen.Id, free.X, free.Y, true));
            }
        }
        // Empty desktops and deleted icons are valid. Do not manufacture missing files.
        bool needsFree = (current.FolderFlags & 4) != 0 && moves.Any(m =>
            !IsAligned(current, current.Displays.Single(d => d.Id == m.MonitorId), new(m.X, m.Y))
            && (now[m.Id].MonitorId!=m.MonitorId || now[m.Id].X!=m.X || now[m.Id].Y!=m.Y));
        return new RestorePlan(saved.Id, current.ConfigKey, exact, moves,
            saved.Icons.Count(i => !now.ContainsKey(i.Id)), newcomers.Count(i=>targets is null || targets.Contains(i.MonitorId)), null) { GridAlignment = needsFree ? false : null };
    }

}
