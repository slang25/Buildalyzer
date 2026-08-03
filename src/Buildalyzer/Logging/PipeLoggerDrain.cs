using System.Threading.Tasks;
using Buildalyzer.Environment;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logging;

/// <summary>Reads a pipe logger server for as long as the build process writing to it is running.</summary>
internal static class PipeLoggerDrain
{
    /// <summary>
    /// How long to keep reading after the build process has exited. The pipe is drained while the build
    /// runs, so anything still in flight at that point is a tail; the bound is here so that a transport
    /// which never reports the end of its stream costs a pause rather than the whole analysis.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to let the read unwind after the server has been disposed under it.</summary>
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(5);

    /// <summary>Reads every event <paramref name="server"/> receives while <paramref name="process"/> runs.</summary>
    /// <remarks>
    /// The read happens on its own thread rather than on the caller's, so that waiting for the build
    /// process never depends on the pipe reaching the end of its stream. A build that exits without ever
    /// writing to the pipe leaves the server holding its own copy of the client handle, so the read would
    /// otherwise wait for an end of file that cannot arrive.
    /// </remarks>
    public static void ReadUntilExit(IPipeLoggerServer server, ProcessRunner process)
    {
        var read = Task.Run(() =>
        {
            try
            {
                server.ReadAll();
            }
            catch (ObjectDisposedException)
            {
                // The server was disposed to bring this read to an end.
            }
        });

        process.WaitForExit();

        // The build process has gone, so no further build submission can connect. This is a no-op on an
        // anonymous pipe, where the read ends by itself when the client closes its handle, but it is what
        // ends the read on a transport that serves more than one connection.
        server.StopListening();

        if (!read.Wait(DrainTimeout))
        {
            // Disposing tears the transport down at once and discards whatever the reader has not handed
            // over yet, which is why it is the backstop rather than the way this normally ends.
            server.Dispose();
            read.Wait(DisposeGrace);
        }

        if (read.IsCompleted)
        {
            // Surface a read failure unwrapped, as reading in line did, rather than as an AggregateException.
            read.GetAwaiter().GetResult();
        }
    }
}
