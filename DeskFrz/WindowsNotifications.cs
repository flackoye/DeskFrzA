using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DeskFrz;

/// <summary>Registered Windows toasts; registration and dependencies load only in resident mode.</summary>
internal sealed class WindowsNotifications : IDisposable
{
    const string Group = "resident", ReviewTag = "review", TestTag = "test";
    readonly string data, session = Guid.NewGuid().ToString("N");
    readonly Action<Action> dispatch;
    readonly Action<ReviewKind?> clicked;
    ToastNotifierCompat? notifier;
    bool initialized, disposed;
    string? currentReview;

    public WindowsNotifications(string data, Action<Action> dispatch, Action<ReviewKind?> clicked)
    { this.data = data; this.dispatch = dispatch; this.clicked = clicked; }

    public event Action<string>? StatusChanged;

    internal static string AppId(string data) =>
        "DeskFrzA." + Fingerprint.Of(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant());
    internal string Arguments(ReviewKind? kind) => session + ":" + (kind?.ToString() ?? "Test");
    internal static bool TryReadArguments(string argument, string session, out ReviewKind? kind)
    {
        kind = null;
        if (!argument.StartsWith(session + ":", StringComparison.Ordinal)) return false;
        var value = argument[(session.Length + 1)..];
        if (value == "Test") return true;
        if (value is not ("Desktop" or "Screen" or "Recovery")) return false;
        kind = Enum.Parse<ReviewKind>(value);
        return true;
    }

    public void Show(string title, string message, ReviewKind? kind)
    {
        if (disposed) return;
        try {
            if (!initialized) {
                // This also isolates multiple --data libraries using the same executable.
                Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId(data)));
                ToastNotificationManagerCompat.OnActivated += Activated;
                initialized = true;
            }
            notifier ??= ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled) {
                Record("Blocked", "系统通知：已被 Windows 禁用", notifier.Setting.ToString());
                return;
            }
            var arguments = Arguments(kind);
            if (kind is not null) currentReview = arguments;
            var xml = new XmlDocument();
            xml.LoadXml(new ToastContentBuilder().AddText(title).AddText(message).GetToastContent().GetContent());
            xml.DocumentElement!.SetAttribute("launch", arguments);
            var toast = new ToastNotification(xml) {
                Tag = kind is null ? TestTag : ReviewTag, Group = Group,
                ExpirationTime = DateTimeOffset.Now.AddHours(1)
            };
            toast.Failed += (_, error) => dispatch(() => {
                if (!disposed) Record("Failed", "系统通知：发送失败", error.ErrorCode.ToString());
            });
            notifier.Show(toast);
            // Submitted proves the API accepted it, not that Windows displayed a banner.
            Record("Submitted", "系统通知：已提交", null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException) {
            Record("Failed", "系统通知：不可用", ex.Message);
        }
    }

    void Activated(ToastNotificationActivatedEventArgsCompat args)
    {
        dispatch(() => {
            if (disposed || !TryReadArguments(args.Argument, session, out var kind)) return;
            if (kind is not null && currentReview != args.Argument) return;
            clicked(kind);
        });
    }

    public void ClearReview()
    {
        currentReview = null;
        Remove(ReviewTag);
    }

    void Remove(string tag)
    {
        if (notifier is null) return;
        try { ToastNotificationManagerCompat.History.Remove(tag, Group); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException) {
            Record("ClearFailed", "系统通知：清理失败", ex.Message);
        }
    }

    void Record(string result, string status, string? error)
    {
        StatusChanged?.Invoke(status);
        // One bounded diagnostic record; no desktop snapshot is logged.
        try {
            File.WriteAllText(Path.Combine(data, "notification-status.json"),
                JsonSerializer.Serialize(new { time = DateTimeOffset.Now, result, error }));
        }
        catch (IOException ex) { System.Diagnostics.Trace.TraceWarning("Notification diagnostic: {0}", ex.Message); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.TraceWarning("Notification diagnostic: {0}", ex.Message); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearReview(); Remove(TestTag);
        if (initialized) ToastNotificationManagerCompat.OnActivated -= Activated;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
