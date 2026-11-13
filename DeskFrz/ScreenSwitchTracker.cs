namespace DeskFrz;

/// <summary>Retain the last observed layout before a primary-screen switch, even without a saved record.</summary>
internal sealed class ScreenSwitchTracker
{
    Snapshot? previous;
    public Snapshot? Source { get; private set; }
    public void Observe(Snapshot current)
    {
        // A disconnected topology cannot retain an old primary-switch action.
        if (current.Displays.Count == 1) Source = null;
        else if (previous is not null && previous.Displays.Single(s => s.Primary).Id != current.Displays.Single(s => s.Primary).Id)
            Source = previous;
        else if (previous is not null && previous.MonitorSet != current.MonitorSet)
            Source = null;
        previous = current;
    }
    public void Clear(Snapshot current) { Source = null; previous = current; }
    public void Reset() { Source = previous = null; }
}
