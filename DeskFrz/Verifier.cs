namespace DeskFrz;

public static class Verifier
{
    public static bool Matches(Snapshot before, RestorePlan plan, Snapshot after)
    {
        if (after.ConfigKey != before.ConfigKey || (after.FolderFlags & 1) != 0) return false;
        var expected = before.Icons.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var move in plan.Moves)
            expected[move.Id] = expected[move.Id] with { X = move.X, Y = move.Y, MonitorId = move.MonitorId };
        if (after.Icons.Count != expected.Count) return false;
        return after.Icons.All(i => expected.TryGetValue(i.Id, out var e)
            && i.X == e.X && i.Y == e.Y && i.MonitorId == e.MonitorId);
    }
}
