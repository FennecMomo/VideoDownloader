using System.Security.Principal;
using System.Windows;

namespace BiliCache;

public partial class App : Application
{
    // Hold the lock for the process lifetime, including child-process and download cleanup.
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        using var identity = WindowsIdentity.GetCurrent();
        instance = new Mutex(false, @"Global\BiliCache-" + identity.User!.Value);
        try
        {
            if (!instance.WaitOne(0))
            {
                StartupUri = null;
                Shutdown();
                return;
            }
        }
        catch (AbandonedMutexException) { /* A previous instance exited. This instance now owns the lock. */ }

        base.OnStartup(e);
    }
}
