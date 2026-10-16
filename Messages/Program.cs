using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Messages;

/// <summary>
/// Single-instance entry point: the app owns a backend on a fixed port, so a second launch
/// (Start menu, toast) is forwarded to the running window instead of starting another backend.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (Services.PackageIdentity.EnsureOrRelaunch(args)) return 0;
        if (RedirectToRunningInstance()) return 0;
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey("NativeMessages.Main");
        if (main.IsCurrent)
        {
            main.Activated += (_, args) => (Application.Current as App)?.OnRedirected(args);
            return false;
        }
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
        return true;
    }
}
