using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace ScottPlot.D3D12.UWP;

public partial class UwpPlotD3D12
{
    private bool LifecycleSubscribed;
    private bool LifecycleSuspended;
    private CoreWindow? ObservedWindow;
    private void SubscribeLifecycle()
    {
        if (LifecycleSubscribed) return;
        Application.Current.Suspending += OnAppSuspending; Application.Current.Resuming += OnAppResuming;
        ObservedWindow = Window.Current?.CoreWindow;
        if (ObservedWindow is not null) ObservedWindow.Closed += OnCoreWindowClosed;
        LifecycleSubscribed = true;
    }
    private void UnsubscribeLifecycle()
    {
        if (!LifecycleSubscribed) return;
        Application.Current.Suspending -= OnAppSuspending; Application.Current.Resuming -= OnAppResuming;
        if (ObservedWindow is not null) ObservedWindow.Closed -= OnCoreWindowClosed;
        ObservedWindow = null; LifecycleSubscribed = false;
    }
    private void OnAppSuspending(object sender, SuspendingEventArgs args)
    {
        SuspendingDeferral deferral = args.SuspendingOperation.GetDeferral();
        try { LifecycleSuspended = true; ReleaseGpu(); }
        finally { deferral.Complete(); }
    }
    private void OnAppResuming(object sender, object args) { LifecycleSuspended = false; Refresh(); }
    private void OnCoreWindowClosed(CoreWindow sender, CoreWindowEventArgs args) { UnsubscribeLifecycle(); ReleaseGpu(); }
}
