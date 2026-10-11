namespace DeskFrz;

/// <summary>Retain the last observed layout before a primary-screen switch, even without a saved record.</summary>
internal sealed class ScreenSwitchTracker
{
    Snapshot? previous;
    public Snapshot? Source { get; private set; }
    public void Observe(Snapshot current)
    {
        if (previous is not null && previous.Displays.Single(s => s.Primary).Id != current.Displays.Single(s => s.Primary).Id)
            Source = previous;
        previous = current;
    }
    public void Clear(Snapshot current) { Source = null; previous = current; }
}
