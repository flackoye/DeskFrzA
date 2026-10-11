using System.Runtime.InteropServices;

namespace DeskFrz.Native;

public sealed class DpiScope : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetThreadDpiAwarenessContext(nint context);
    readonly nint previous;
    public DpiScope()
    {
        previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0) throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose()
    {
        if (SetThreadDpiAwarenessContext(previous) == 0) throw new System.ComponentModel.Win32Exception();
    }
}
