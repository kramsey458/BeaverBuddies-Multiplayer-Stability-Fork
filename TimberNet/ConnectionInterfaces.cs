using System;

namespace TimberNet
{
    /// <summary>
    /// Optional. A transport whose connection finishes in the background (for example a relayed Steam
    /// connection) rather than inside <see cref="ISocketStream.ConnectAsync"/>. The caller waits on a
    /// worker thread, never the game thread, because the transport may need the game thread to finish.
    /// </summary>
    public interface IConnectionAwaitable
    {
        /// <summary>Blocks until the connection is usable. Throws IOException if it failed or timed out.</summary>
        void WaitForConnection(int timeoutMilliseconds);
    }

    /// <summary>
    /// Optional. A transport whose Write can wait for the other end to read (a direct TCP connection whose send buffer is
    /// full). The host writes to such a guest from a thread of that guest's own (<see cref="SendLane"/>), never from its
    /// game thread, so one guest that stops reading cannot stop the host and every other guest. A transport whose writes
    /// only queue (Steam) is written to directly, which keeps a tick's events in the pump that follows the tick.
    /// </summary>
    public interface IBlockingWrites
    {
    }

    /// <summary>Optional. A transport that can explain why its connection ended.</summary>
    public interface IFailureDescriber
    {
        string? FailureReason { get; }
    }
}
