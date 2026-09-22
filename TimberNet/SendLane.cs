using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TimberNet
{
    /// <summary>
    /// The host's frames for one guest whose connection can block (<see cref="IBlockingWrites"/>, a direct TCP link), written
    /// in the order they were posted by a thread of that guest's own. Posting never waits for the network, so a guest that
    /// stops reading (its computer asleep, its link gone without a word) no longer stops the host's game thread in the
    /// middle of a tick, and with it every other guest. How long this guest has been stuck on one frame, and how much waits
    /// for it, tell the host when to drop it (<see cref="IsStalled"/>).
    /// </summary>
    public sealed class SendLane
    {
        private readonly struct Frame
        {
            public readonly byte[] Wire;
            public readonly string Type;
            public readonly int Tick;
            public Frame(byte[] wire, string type, int tick) { Wire = wire; Type = type; Tick = tick; }
        }

        private readonly Queue<Frame> queue = new Queue<Frame>();
        private readonly object gate = new object();
        private readonly Action<byte[], string, int> write;
        private long queuedBytes;
        // When the frame being written was taken from the queue, in NowMs; -1 while nothing is being written.
        private long writingSinceMs = -1;
        private bool closed;

        public static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        /// <param name="name">For the thread's name.</param>
        /// <param name="write">Writes one frame to the guest; it reports its own failures (and closes the stream).</param>
        public SendLane(string name, Action<byte[], string, int> write)
        {
            this.write = write;
            var thread = new Thread(Run) { IsBackground = true, Name = "BeaverBuddies send to " + name };
            try { thread.Priority = ThreadPriority.AboveNormal; } catch (Exception) { }
            thread.Start();
        }

        /// <summary>Queues a frame, after every frame posted before it. Never waits for the network.</summary>
        public void Post(byte[] wire, string type, int tick)
        {
            lock (gate)
            {
                if (closed) return;
                queue.Enqueue(new Frame(wire, type, tick));
                queuedBytes += wire.Length;
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>
        /// True when the guest has taken no frame for more than <paramref name="limitMs"/> (one write has been waiting that
        /// long), or more than <paramref name="maxQueuedBytes"/> waits for it.
        /// </summary>
        public bool IsStalled(long nowMs, long limitMs, long maxQueuedBytes)
        {
            lock (gate)
            {
                return (writingSinceMs >= 0 && nowMs - writingSinceMs > limitMs) || queuedBytes > maxQueuedBytes;
            }
        }

        /// <summary>Waits until everything posted has been written (true), the lane closes, or the time is up (false).</summary>
        public bool WaitUntilEmpty(int timeoutMs)
        {
            long deadline = NowMs + timeoutMs;
            lock (gate)
            {
                while (!closed && (queue.Count > 0 || writingSinceMs >= 0))
                {
                    long left = deadline - NowMs;
                    if (left <= 0) return false;
                    Monitor.Wait(gate, (int)Math.Min(left, int.MaxValue));
                }
                return true;
            }
        }

        /// <summary>Stops the lane: what is still queued is dropped. A write under way ends when its stream is closed.</summary>
        public void Close()
        {
            lock (gate)
            {
                closed = true;
                queue.Clear();
                queuedBytes = 0;
                Monitor.PulseAll(gate);
            }
        }

        private void Run()
        {
            while (true)
            {
                Frame next;
                lock (gate)
                {
                    while (queue.Count == 0 && !closed) Monitor.Wait(gate);
                    if (closed) return;
                    next = queue.Dequeue();
                    queuedBytes -= next.Wire.Length;
                    writingSinceMs = NowMs;
                }
                try { write(next.Wire, next.Type, next.Tick); }
                catch (Exception) { /* the writer reports its own failures; the lane carries on or is closed */ }
                lock (gate)
                {
                    writingSinceMs = -1;
                    Monitor.PulseAll(gate);
                }
            }
        }
    }
}
