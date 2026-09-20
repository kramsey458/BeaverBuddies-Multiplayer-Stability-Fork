using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace TimberNet
{ 

    public class TimberServer : TimberNetBase
    {

        private readonly List<ISocketStream> clients = new List<ISocketStream>();
        private readonly ConcurrentDictionary<ISocketStream, ConcurrentQueue<JObject>> queuedMessages =
            new ConcurrentDictionary<ISocketStream, ConcurrentQueue<JObject>>();

        // Player activity is presentation-only and deliberately kept out of queuedMessages' lock,
        // so a slow gameplay send can never stall a guest's receive thread.
        private readonly ConcurrentDictionary<ISocketStream, int> playerIds = new ConcurrentDictionary<ISocketStream, int>();
        private readonly ConcurrentDictionary<ISocketStream, ActivityChannel> activityChannels =
            new ConcurrentDictionary<ISocketStream, ActivityChannel>();
        private int lastPlayerId;

        // Chat: the host numbers every message and keeps the whole conversation, so a guest who joins late can be
        // sent all of it. chatGate makes "publish a message" and "send the history to a new guest" one step each, so
        // a message is either in the history a joining guest gets or queued behind it, never both and never neither.
        private readonly object chatGate = new object();
        private readonly ConcurrentDictionary<ISocketStream, ChatRateLimiter> chatLimits =
            new ConcurrentDictionary<ISocketStream, ChatRateLimiter>();
        private int lastChatSequence;

        // How often the host pings each guest and publishes the roster. Adjustable so tests need not wait.
        public static int StatusIntervalMs = 1000;
        private readonly ConcurrentDictionary<ISocketStream, RttTracker> trackers = new ConcurrentDictionary<ISocketStream, RttTracker>();
        // How many ticks behind the host each guest was at its last reply. Written on network threads.
        private readonly ConcurrentDictionary<ISocketStream, int> guestTicksBehind = new ConcurrentDictionary<ISocketStream, int>();
        // The frames per second each guest last reported. Removed again when a reply carries none.
        private readonly ConcurrentDictionary<ISocketStream, int> guestFps = new ConcurrentDictionary<ISocketStream, int>();
        private double nextStatusAtMs;

        private readonly ISocketListener listener;

        private Func<Task<byte[]>> mapProvider;
        private Func<JObject>? initEventProvider;

        public int ClientCount { get { lock (queuedMessages) return clients.Count; } }

        private string? errorMessage = null;
        public bool IsAcceptingClients => errorMessage == null;

        public List<string?> GetConnectedClients()
        {
            lock (queuedMessages) return clients.Select(c => c.Name).ToList();
        }

        public TimberServer(ISocketListener listener, Func<Task<byte[]>> mapProvider, Func<JObject>? initEventProvider)
        {
            this.listener = listener;
            this.mapProvider = mapProvider;
            this.initEventProvider = initEventProvider;
        }

        public void UpdateProviders(Func<Task<byte[]>> mapProvider, Func<JObject>? initEventProvider)
        {
            this.mapProvider = mapProvider;
            this.initEventProvider = initEventProvider;
        }

        protected override void ReceiveEvent(JObject message)
        {
            message[TICKS_KEY] = TickCount;
            base.ReceiveEvent(message);
        }

        public override void Start()
        {
            base.Start();

            listener.Start();
            Log("Server started listening");
            
            Task.Run(() =>
            {
                // TODO: I have a suspicion that this while plus the catch/continue below
                // is responsible for the server hanging sometimes on a connection that's dropped.
                // Logging now to see if I can catch it.
                while (!IsStopped)
                {
                    ISocketStream client;
                    try
                    {
                        Log("Accepting client...");
                        client = listener.AcceptClient();
                    } catch (Exception e)
                    {
                        Log("Error accepting client.");
                        Log(e.StackTrace);
                        continue;
                    }
                    Task.Run(async () =>
                    {
                        try
                        {
                            if (!IsAcceptingClients)
                            {
                                SendErrorMessage(client);
                                client.Close();
                                return;
                            }

                            if (CompatibilityIdentity != null) RunCompatibilityHandshake(client, true);
                            if (IsStopped || !IsAcceptingClients) { client.Close(); return; }
                            await SendMap(client);
                            SendState(client);
                            if (initEventProvider != null)
                            {
                                JObject initEvent = initEventProvider();
                                // Send the event before finishing queueing
                                // so it is guaranteed to arrive first.
                                // (This also sends it to other clients.)
                                DoUserInitiatedEvent(initEvent, true);
                            }
                            FinishQueuing(client);

                            // This must come last - it is an infinite loop
                            // until the client disconnects
                            StartListening(client, false);
                        }
                        catch (Exception error) { HandleConnectionFailure(client, "Connection rejected: " + error.Message); }
                    });
                }
            });
        }

        public void StopAcceptingClients(string errorMessage)
        {
            this.errorMessage = errorMessage;
        }

        private void StartQueuing (ISocketStream client)
        {
            lock (queuedMessages)
            {
                if (IsStopped) { client.Close(); throw new IOException("Session closed while joining."); }
                queuedMessages.TryAdd(client, new ConcurrentQueue<JObject>());
                clients.Add(client);
                // The host is player 0; the host, not the guest, chooses each guest's id.
                playerIds[client] = Interlocked.Increment(ref lastPlayerId);
                trackers[client] = new RttTracker(RttTracker.NowMs);
            }
        }

        private void RemoveActivity(ISocketStream client)
        {
            playerIds.TryRemove(client, out _);
            trackers.TryRemove(client, out _);
            guestTicksBehind.TryRemove(client, out _);
            guestFps.TryRemove(client, out _);
            chatLimits.TryRemove(client, out _);
            if (activityChannels.TryRemove(client, out ActivityChannel? channel)) channel.Close();
        }

        protected override void HandleStatusFrame(ISocketStream source, string type, JObject message)
        {
            // Only a guest's reply to our probe means anything to the host.
            if (type != StatusFrames.ReplyType || !StatusFrames.TryParseReply(message, out int sequence, out int? tick, out int? fps)) return;
            if (!trackers.TryGetValue(source, out RttTracker? tracker)) return;
            tracker.OnReply(sequence, RttTracker.NowMs);
            // The reply left the guest about half a round trip ago, which is a fraction of a tick.
            // A guest that has not ticked yet is loading, not behind. Without this a rehost compared the old
            // session's tick count with the joining guest's zero and made the host wait for it (1.0.6).
            if (tick != null && tick.Value > 0) guestTicksBehind[source] = Math.Max(0, TickCount - tick.Value);
            // A guest leaves the frame rate out while its window is in the background, so an old figure must go.
            if (fps != null) guestFps[source] = fps.Value; else guestFps.TryRemove(source, out _);
        }

        /// <summary>
        /// The lowest frame rate any connected guest last reported, or null if none has reported one. Used to ease
        /// the host's speed when the host has chosen a frame rate floor.
        /// </summary>
        public int? WorstGuestFps
        {
            get
            {
                int? worst = null;
                foreach (var pair in guestFps)
                {
                    if (!pair.Key.Connected) continue;
                    if (worst == null || pair.Value < worst.Value) worst = pair.Value;
                }
                return worst;
            }
        }

        /// <summary>
        /// The largest number of ticks any connected guest was behind the host at its last reply, or null if no
        /// guest has reported one. Used to ease the host's speed when a guest cannot keep up.
        /// </summary>
        public int? WorstGuestTicksBehind
        {
            get
            {
                int? worst = null;
                foreach (var pair in guestTicksBehind)
                {
                    if (!pair.Key.Connected) continue;
                    if (worst == null || pair.Value > worst.Value) worst = pair.Value;
                }
                return worst;
            }
        }

        protected override void OnUpdate()
        {
            double now = RttTracker.NowMs;
            if (now < nextStatusAtMs) return;
            nextStatusAtMs = now + StatusIntervalMs;
            // Only guests that have finished joining have a channel, so nothing is sent mid-transfer.
            foreach (var pair in activityChannels)
            {
                ISocketStream stream = pair.Key;
                if (!stream.Connected || !trackers.TryGetValue(stream, out RttTracker? tracker)) continue;
                if (!playerIds.TryGetValue(stream, out int id)) continue;
                // Built at write time, so the probe's timestamp is when it really leaves.
                pair.Value.PostFrame("probe", () => StatusFrames.Probe(tracker.BeginProbe(RttTracker.NowMs)));
                pair.Value.PostFrame("roster", () => StatusFrames.Roster(id, BuildPeerStatuses()));
            }
        }

        private List<PeerStatus> BuildPeerStatuses()
        {
            double now = RttTracker.NowMs;
            var peers = new List<PeerStatus>();
            foreach (var pair in activityChannels)
            {
                ISocketStream stream = pair.Key;
                if (!stream.Connected || !playerIds.TryGetValue(stream, out int id) || !trackers.TryGetValue(stream, out RttTracker? tracker)) continue;
                PeerStatus peer = tracker.Snapshot(id, (stream as ITransportInfo)?.TransportName ?? "", now);
                if (guestTicksBehind.TryGetValue(stream, out int behind)) peer = peer.WithTicksBehind(behind);
                if (guestFps.TryGetValue(stream, out int fps)) peer = peer.WithFps(fps);
                peers.Add(peer);
            }
            peers.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
            return peers;
        }

        public override NetworkStatus GetNetworkStatus() =>
            new NetworkStatus(true, IsStopped, 0, null, BuildPeerStatuses());

        protected override void HandleActivity(ISocketStream source, PlayerActivity activity)
        {
            // Frames from a connection that has not been admitted are ignored.
            if (!playerIds.TryGetValue(source, out int id)) return;
            PlayerActivity assigned = activity.WithPlayerId(id);
            base.HandleActivity(source, assigned);
            RelayActivity(assigned, source);
        }

        public override void SendActivity(PlayerActivity activity)
        {
            if (IsStopped) return;
            RelayActivity(activity.WithPlayerId(0), null);
        }

        private void RelayActivity(PlayerActivity activity, ISocketStream? except)
        {
            foreach (var pair in activityChannels)
            {
                if (ReferenceEquals(pair.Key, except)) continue;
                if (!pair.Key.Connected) { RemoveActivity(pair.Key); continue; }
                pair.Value.Post(activity);
            }
        }

        protected override void HandleChat(ISocketStream source, IReadOnlyList<ChatMessage> messages, bool isHistory)
        {
            // History only flows from the host to a guest, and a guest sends one message at a time.
            if (isHistory || messages.Count != 1) return;
            // Frames from a connection that has not been admitted are ignored.
            if (!playerIds.TryGetValue(source, out int id)) return;
            // A guest that talks too fast is dropped, not disconnected: chat is optional.
            if (!chatLimits.GetOrAdd(source, _ => new ChatRateLimiter()).TryTake(RttTracker.NowMs)) return;
            Publish(messages[0], id);
        }

        public override bool SendChat(string name, string color, string text)
        {
            if (IsStopped || !ChatMessage.TryCreate(name, color, text, out ChatMessage? message) || message == null) return false;
            Publish(message, 0);
            return true;
        }

        /// <summary>Numbers a message, keeps it, and queues it for every guest that has finished joining.</summary>
        private void Publish(ChatMessage message, int playerId)
        {
            lock (chatGate)
            {
                ChatMessage stamped = message.Stamped(++lastChatSequence, playerId);
                Chat.Add(stamped);
                JObject frame = stamped.ToJson();
                foreach (var pair in activityChannels)
                {
                    if (!pair.Key.Connected) { RemoveActivity(pair.Key); continue; }
                    pair.Value.PostOrdered(frame);
                }
            }
        }

        // The map, state and init event are already written, so this guest can now receive activity and chat.
        private void OpenActivityLane(ISocketStream client)
        {
            ActivityChannel channel = CreateActivityChannel(client);
            lock (chatGate)
            {
                // Opened and given the history in one step: a message published after this is queued behind it.
                activityChannels[client] = channel;
                foreach (JObject frame in ChatMessage.HistoryFrames(Chat.All())) channel.PostOrdered(frame);
            }
        }

        private void FinishQueuing(ISocketStream client)
        {
            // Log("finishing queuing");
            lock(queuedMessages)
            {
                if (queuedMessages.TryGetValue(client, out ConcurrentQueue<JObject> queue))
                {
                    // Log($"Found {queue.Count} messages");
                    while (queue.TryDequeue(out JObject message))
                    {
                        // Log(message.ToString());
                        SendEvent(client, message);
                    }
                    queuedMessages.TryRemove(client, out _);
                    if (!IsStopped) OpenActivityLane(client);
                }
                else
                {
                    Log("Warning! Missing client!");
                }
            }
        }

        private void SendErrorMessage(ISocketStream client)
        {
            SendLength(client, 0);
            byte[] bytes = MessageToBuffer(errorMessage!);
            // TODO: Not sure this makes sense for Steam
            SendDataWithLength(client, bytes);
        }

        private async Task SendMap(ISocketStream client)
        { 
            Task<byte[]> task = mapProvider();
            Log("Waiting for map...");
            byte[] mapBytes = await task;

            // TODO: This may happen a bit early - it seems possible for
            // events from a prior frame to get queued. Maybe just need to filter
            // them on the client side.
            // Start recording messages as soon as the map is saved,
            // while the map is sending
            StartQueuing(client);

            Log($"Sending map with length {mapBytes.Length}");
            SendDataWithLength(client, mapBytes);

            Log($"Sent map with length {mapBytes.Length} and Hash: {GetHashCode(mapBytes).ToString("X8")}");
        }

        private void SendState(ISocketStream client)
        {
            JObject message = new JObject();
            message[TICKS_KEY] = 0;
            message[TYPE_KEY] = SET_STATE_EVENT;
            message["hash"] = Hash;
            // Send directly - don't queue
            SendEvent(client, message);
        }

        void DoUserInitiatedEvent(JObject message, bool sendNow)
        {
            base.DoUserInitiatedEvent(message);
            SendEventToClients(message, sendNow);
        }

        public override void DoUserInitiatedEvent(JObject message)
        {
            DoUserInitiatedEvent(message, false);
        }

        private void SendEventToClients(JObject message, bool sendNow)
        {
            lock (queuedMessages)
            {
                for (int i = clients.Count - 1; i >= 0; i--)
                {
                    if (!clients[i].Connected)
                    {
                        queuedMessages.TryRemove(clients[i], out _);
                        RemoveActivity(clients[i]);
                        clients.RemoveAt(i);
                    }
                }
                // Share the join/close lock across enumeration and mutation.
                clients.ForEach(client =>
                {
                    if (sendNow)
                    {
                        SendEvent(client, message);
                    }
                    else
                    {
                        QueueOrSentToClient(client, message);
                    }
                });
            }
        }

        private void QueueOrSentToClient(ISocketStream client, JObject message)
        {
            if (!client.Connected) return;

            if (queuedMessages.TryGetValue(client, out ConcurrentQueue<JObject> queue))
            {
                queue.Enqueue(message);
            }
            else
            {
                SendEvent(client, message);
            }
        }

        public override void AbortSession(string reason)
        {
            try
            {
                lock (queuedMessages)
                    foreach (var client in clients.ToArray()) SendSessionFault(client, reason);
            }
            finally { Close(); }
        }

        public override void Close()
        {
            base.Close();
            foreach (var pair in activityChannels) pair.Value.Close();
            try
            {
                lock (queuedMessages) clients.ForEach(client => client.Close());
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }
            try
            {
                listener.Stop();
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }  
        }

        public void SendHeartbeat()
        {
            JObject message = new JObject();
            message[TICKS_KEY] = TickCount;
            message[TYPE_KEY] = HEARTBEAT_EVENT;
            // Simulate the user doing this
            DoUserInitiatedEvent(message);
        }
    }
}
