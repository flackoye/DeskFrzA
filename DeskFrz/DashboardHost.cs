using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskFrz;

/// <summary>The dashboard is disposable; desktop protection runs independently.</summary>
internal sealed class DashboardHost : IDisposable
{
    readonly Window window;
    readonly string address, profile;
    readonly bool dev;
    readonly Action<Exception> failure;
    readonly Func<Task<CoreWebView2Environment>> createEnvironment;
    readonly DispatcherTimer releaseTimer;
    WebView2? view;
    bool disposed, reviewPending;
    ReviewKind reviewKind;
    public bool IsReady { get; private set; }
    public bool HasLoaded { get; private set; }

    public DashboardHost(Window window, string address, string data, bool dev, Action<Exception> failure,
        Func<Task<CoreWebView2Environment>>? createEnvironment = null)
    {
        this.window = window; this.address = address; this.dev = dev; this.failure = failure;
        profile = Path.Combine(data, "webview");
        this.createEnvironment = createEnvironment ?? (() => CoreWebView2Environment.CreateAsync(null, profile));
        // Brief hide/show cycles reuse the page; a sustained background stay releases it.
        releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        releaseTimer.Tick += ReleaseWhenHidden;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
    }

    bool Foreground => window.IsVisible && window.WindowState != WindowState.Minimized;
    public void EnsureForegroundView() => UpdateVisibility();
    void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateVisibility();
    void OnStateChanged(object? sender, EventArgs e) => UpdateVisibility();
    void UpdateVisibility()
    {
        if (disposed) return;
        if (Foreground) { releaseTimer.Stop(); EnsureView(); }
        else releaseTimer.Start();
    }

    async void EnsureView()
    {
        if (disposed || view is not null) return;
        var current = new WebView2();
        view = current;
        window.Content = current;
        try {
            var environment = await createEnvironment();
            if (disposed || view != current) return;
            await current.EnsureCoreWebView2Async(environment);
            if (disposed || view != current) return;
            var core = current.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = dev;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.NavigationStarting += (_, e) => {
                if (!e.Uri.StartsWith(address + "/", StringComparison.Ordinal)) { e.Cancel = true; OpenExternal(e.Uri); }
            };
            core.NavigationCompleted += (_, e) => {
                if (disposed || view != current) return;
                IsReady = e.IsSuccess;
                HasLoaded |= e.IsSuccess;
                if (IsReady && reviewPending) SendReviewRequest();
            };
            current.Source = new Uri(address);
        }
        catch (Exception ex) {
            // Closing during asynchronous initialization invalidates only that view.
            if (!disposed && view == current) {
                ReleaseView();
                failure(ex);
            }
        }
    }

    public void RequestChangeReview(ReviewKind kind)
    {
        reviewKind = kind;
        reviewPending = true;
        if (IsReady) SendReviewRequest();
    }
    public void CancelChangeReview() => reviewPending = false;
    async void SendReviewRequest()
    {
        if (view?.CoreWebView2 is not { } core || !IsReady) return;
        reviewPending = false;
        string kind = System.Text.Json.JsonSerializer.Serialize(reviewKind.ToString().ToLowerInvariant());
        try { await core.ExecuteScriptAsync($"window.dispatchEvent(new CustomEvent('deskfrza-review-changes',{{detail:{kind}}}))"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
            reviewPending = true;
            System.Diagnostics.Trace.TraceWarning("Change review deferred: {0}", ex.Message);
        }
    }

    void ReleaseWhenHidden(object? sender, EventArgs e)
    {
        releaseTimer.Stop();
        if (!Foreground) ReleaseView();
    }
    void ReleaseView()
    {
        var previous = view;
        view = null;
        IsReady = false;
        window.Content = null;
        previous?.Dispose();
    }
    static void OpenExternal(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443 || uri.UserInfo.Length != 0) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开 GitHub"); }
    }
    void OnClosed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        releaseTimer.Stop();
        releaseTimer.Tick -= ReleaseWhenHidden;
        window.IsVisibleChanged -= OnVisibilityChanged;
        window.StateChanged -= OnStateChanged;
        window.Closed -= OnClosed;
        ReleaseView();
    }
}
