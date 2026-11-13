using DeskFrz.Native;

namespace DeskFrz;

internal interface IDesktopSession : IDisposable
{
    Snapshot Capture();
    void Apply(List<Move> moves, Position origin);
    void SetGridAlignment(bool enabled);
    void DisableAutoArrange();
}

internal interface IDisplayConfiguration
{
    bool Supports(List<Display> current);
    void Validate(List<Display> current);
    void Restore(List<Display> current);
}

internal interface IDesktopRuntime
{
    IDisposable EnterDpiScope();
    IDesktopSession Open();
    IDisplayConfiguration CaptureConfiguration(List<Display> displays);
    DateTimeOffset Now { get; }
    void Wait(int milliseconds);
}

internal sealed class WindowsDesktopRuntime : IDesktopRuntime
{
    public IDisposable EnterDpiScope() => new DpiScope();
    public IDesktopSession Open() => new Desktop();
    public IDisplayConfiguration CaptureConfiguration(List<Display> displays) => Displays.CaptureConfiguration(displays);
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
}
