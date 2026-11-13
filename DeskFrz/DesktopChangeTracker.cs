namespace DeskFrz;

internal enum ReviewKind { Desktop, Screen, Recovery }

/// <summary>Debounce icon edits and keep notification deduplication outside the disposable page.</summary>
internal sealed class DesktopChangeTracker
{
    string? baseline, candidate, notified;
    DateTimeOffset stableSince, lastNotice;
    public bool IsDirty => candidate is not null;

    public void Accept(string? key)
    {
        baseline = key;
        candidate = notified = null;
        lastNotice = default;
    }

    public bool Observe(string key, DateTimeOffset now)
    {
        if (baseline is null) { Accept(key); return false; }
        if (key == baseline) { candidate = notified = null; return false; }
        if (candidate != key) { candidate = key; stableSince = now; return false; }
        if (now - stableSince < TimeSpan.FromSeconds(1) || notified == key
            || (lastNotice != default && now - lastNotice < TimeSpan.FromSeconds(10))) return false;
        notified = key;
        lastNotice = now;
        return true;
    }
}
