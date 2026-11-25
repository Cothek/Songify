using System.IO;
using System.Runtime.InteropServices;

namespace Songify.App;

/// <summary>Moves files to the Recycle Bin, so a replaced original can be restored from there.</summary>
public static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400,
        FofWantNukeWarning = 0x4000;

    /// <summary>One at a time, so Windows never shows two "delete for good?" prompts at once.</summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// Moves <paramref name="path"/> to the Recycle Bin. Where a drive has none (a network drive), Windows asks before
    /// deleting it for good, in front of <paramref name="owner"/>. Built for 64-bit only (the struct layout differs on x86).
    /// </summary>
    /// <exception cref="IOException">It's still there: open in another app, or the user said no.</exception>
    public static void Send(string path, IntPtr owner)
    {
        var operation = new ShFileOperation
        {
            Window = owner,
            Function = FoDelete,
            From = Path.GetFullPath(path) + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofWantNukeWarning,
        };
        int result;
        lock (Gate) result = SHFileOperationW(ref operation);
        if (result != 0 || operation.AnyOperationsAborted || File.Exists(path))
            throw new IOException(
                $"Couldn't move {Path.GetFileName(path)} to the Recycle Bin. Close it in any app that's playing it, and try again.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOperation operation);
}
