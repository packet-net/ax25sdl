using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Packet.Sdl.TlaCheck;

/// <summary>
/// A named pipe in place of a Coverage run's states.dump, so the arms are read
/// as TLC writes the states and nothing large touches the disk. TLC opens
/// the dump file for writing (it does not delete or rename it), so a FIFO
/// at that path receives the dump. On a platform without FIFOs (Windows), or
/// where one cannot be made, <see cref="TryCreate"/> says so and the caller
/// falls back to the file.
/// </summary>
internal static class DumpPipe
{
    /// <summary>Make a FIFO at <paramref name="path"/>, replacing any file there; false when FIFOs are not available.</summary>
    public static bool TryCreate(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        File.Delete(path);
        try
        {
            return mkfifo(CPath(path), 0x180) == 0; // 0600
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read the FIFO at <paramref name="path"/> to its end on a background
    /// thread, collecting the arms. The pipe is always read to the end, even
    /// if collecting fails (the failure is rethrown after), so the writer
    /// never blocks on a full pipe.
    /// </summary>
    public static Task<HashSet<string>> Drain(string path) =>
        Task.Factory.StartNew(() => DrainNow(path), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static HashSet<string> DrainNow(string path)
    {
        var arms = new HashSet<string>(StringComparer.Ordinal);
        // Blocks until a writer opens the pipe (TLC, or Finish when TLC never did).
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var reader = new StreamReader(stream);
        ExceptionDispatchInfo? failure = null;
        try
        {
            Tlc.ReadArms(reader, arms);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
            var rest = new char[1 << 16];
            try
            {
                while (reader.Read(rest) > 0) { }
            }
            catch (IOException) { }
        }
        failure?.Throw();
        return arms;
    }

    /// <summary>
    /// Once the writer has exited (or was killed, or never started): wait for
    /// the reader to finish, then remove the FIFO. A reader still waiting to
    /// open the pipe, because TLC failed before opening the dump, is let
    /// through by a writer opened and closed at once, so it sees the end of
    /// the pipe; that is retried until the reader is done, so it cannot hang.
    /// </summary>
    public static HashSet<string> Finish(string path, Task<HashSet<string>> reader)
    {
        try
        {
            while (!reader.Wait(TimeSpan.FromMilliseconds(100))) Release(path);
        }
        catch (AggregateException) { }
        File.Delete(path);
        return reader.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Open and close the FIFO for writing without blocking: a no-op when no
    /// reader is waiting. Close-on-exec, so a TLC started meanwhile for another
    /// cell cannot inherit this write end and hold the reader open.
    /// </summary>
    private static void Release(string path)
    {
        var fd = open(CPath(path), OWrOnly | ONonBlock | OCloExec);
        if (fd >= 0) _ = close(fd);
    }

    /// <summary>A path as libc takes it: UTF-8, NUL-terminated.</summary>
    private static byte[] CPath(string path) => System.Text.Encoding.UTF8.GetBytes(path + "\0");

    private const int OWrOnly = 1;

    private static int ONonBlock => OperatingSystem.IsLinux() ? 0x800 : 0x4;

    private static int OCloExec => OperatingSystem.IsLinux() ? 0x80000 : 0x1000000;

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(byte[] path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
