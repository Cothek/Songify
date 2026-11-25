namespace Songify.Core.Infrastructure;

/// <summary>The single-instance lock (<see cref="AppPaths.SingleInstanceMutexName"/>).</summary>
public static class InstanceLock
{
    /// <summary>
    /// Takes the lock if no one holds it: the owned mutex (release it with ReleaseMutex, then dispose it), or null.
    /// Whether the mutex merely exists doesn't matter: an update's installer may have it open without holding it.
    /// </summary>
    public static Mutex? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero)) return mutex;
        }
        catch (AbandonedMutexException)
        {
            return mutex; // its owner died without releasing it; it's ours now
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>Releases and closes a lock from <see cref="TryAcquire"/>.</summary>
    public static void Release(Mutex mutex)
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
