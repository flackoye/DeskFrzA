using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskFrz;

/// <summary>Optional background lifetime and native Windows change reminders.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly Application application;
    readonly Window window;
    readonly Drawing.Icon icon, pendingIcon;
    readonly string data;
    readonly Forms.ContextMenuStrip menu;
    readonly Forms.NotifyIcon notification;
    readonly Forms.ToolStripItem reviewItem;
    readonly Forms.ToolStripItem notificationStatus;
    WindowsNotifications? toasts;
    bool exiting, disposed, noticePending;
    ReviewKind reviewKind;

    public TrayIcon(Application application, Window window, string data, bool backgroundEnabled = false)
    {
        this.application = application;
        this.window = window;
        this.data = data;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("DeskFrzA.Tray.ico")
            ?? throw new InvalidOperationException("缺少 DeskFrzA 托盘图标。");
        using var source = new Drawing.Icon(stream);
        icon = (Drawing.Icon)source.Clone();
        pendingIcon = CreatePendingIcon(icon);
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 DeskFrzA", null, (_, _) => ShowWindow());
        reviewItem = menu.Items.Add("查看桌面改动", null, (_, _) => ReviewChanges());
        reviewItem.Visible = false;
        menu.Items.Add("发送测试通知", null, (_, _) => SendNotification("DeskFrzA 通知测试", "系统通知可用。点击此通知打开 DeskFrzA。", null));
        notificationStatus = menu.Items.Add("系统通知：尚未发送");
        notificationStatus.Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());
        notification = new Forms.NotifyIcon {
            Icon = icon, Text = window.Title, ContextMenuStrip = menu
        };
        notification.DoubleClick += (_, _) => ShowWindow();
        window.Closing += OnClosing;
        application.SessionEnding += OnSessionEnding;
        application.Exit += OnExit;
        SetBackgroundEnabled(backgroundEnabled);
    }

    internal bool IsVisible => notification.Visible;
    public event Action? Opened;
    public event Action<ReviewKind>? ReviewRequested;
    public bool BackgroundEnabled { get; private set; }

    public void SetBackgroundEnabled(bool enabled)
    {
        BackgroundEnabled = enabled;
        notification.Visible = enabled;
        application.ShutdownMode = enabled ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose;
        if (!enabled) {
            ClearNotice();
            toasts?.Dispose(); toasts = null;
            if (!window.IsVisible && window.IsLoaded) ShowWindow();
        }
    }

    public void Notify(string title, string message, ReviewKind kind)
    {
        if (disposed || exiting || !BackgroundEnabled) return;
        reviewItem.Visible = true;
        noticePending = true;
        reviewKind = kind;
        notification.Icon = pendingIcon;
        notification.Text = "DeskFrzA · 有桌面改动待处理";
        SendNotification(title, message, kind);
    }

    void SendNotification(string title, string message, ReviewKind? kind)
    {
        if (disposed || exiting || !BackgroundEnabled) return;
        if (toasts is null) {
            toasts = new WindowsNotifications(data, action => {
                if (!disposed && !exiting && !application.Dispatcher.HasShutdownStarted)
                    application.Dispatcher.BeginInvoke(action);
            }, clicked => {
                if (disposed || exiting || !BackgroundEnabled) return;
                if (clicked is null) ShowWindow();
                else ReviewChanges(clicked);
            });
            toasts.StatusChanged += status => notificationStatus.Text = status;
        }
        toasts.Show(title, message, kind);
    }

    public void ClearNotice()
    {
        if (!noticePending) return;
        noticePending = false; reviewItem.Visible = false;
        notification.Icon = icon; notification.Text = window.Title;
        toasts?.ClearReview();
    }
    void ReviewChanges(ReviewKind? clicked = null)
    {
        if (!BackgroundEnabled || !noticePending || disposed || exiting) return;
        if (clicked is not null && clicked != reviewKind) return;
        ShowWindow();
        ReviewRequested?.Invoke(reviewKind);
    }

    static Drawing.Icon CreatePendingIcon(Drawing.Icon source)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var bitmap = new Drawing.Bitmap(size.Width, size.Height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap)) {
            graphics.DrawIcon(source, new Drawing.Rectangle(0, 0, size.Width, size.Height));
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float diameter = Math.Max(6, size.Width * .42f), left = size.Width - diameter, top = size.Height - diameter;
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(226, 117, 51));
            using var border = new Drawing.Pen(Drawing.Color.FromArgb(248, 246, 239), Math.Max(1, size.Width / 16f));
            graphics.FillEllipse(brush, left, top, diameter - 1, diameter - 1);
            graphics.DrawEllipse(border, left, top, diameter - 1, diameter - 1);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public void ShowWindow()
    {
        if (exiting || disposed) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        Opened?.Invoke();
    }

    public void Exit(int exitCode = 0)
    {
        if (exiting || disposed) return;
        exiting = true;
        application.Shutdown(exitCode);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting || !BackgroundEnabled) return;
        e.Cancel = true;
        window.Hide();
    }

    void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => exiting = true;
    void OnExit(object sender, ExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Closing -= OnClosing;
        application.SessionEnding -= OnSessionEnding;
        application.Exit -= OnExit;
        notification.Visible = false;
        toasts?.Dispose();
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
        pendingIcon.Dispose();
    }
}
