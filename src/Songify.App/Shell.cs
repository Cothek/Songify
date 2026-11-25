using System.Diagnostics;

namespace Songify.App;

/// <summary>Opens links and folders the way Windows would on a double-click.</summary>
public static class Shell
{
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
