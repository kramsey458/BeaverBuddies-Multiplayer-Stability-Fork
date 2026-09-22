using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using BeaverBuddies.Events;
using TimberNet;
using BeaverBuddies.Steam;

namespace BeaverBuddies.IO
{
    public abstract class NetIOBase<T> : EventIO where T : TimberNetBase
    {

        public T NetBase { get; protected set; }
        public abstract bool RecordReplayedEvents { get; }
        public abstract bool ShouldSendHeartbeat { get; }
        public abstract UserEventBehavior UserEventBehavior { get; }
        public bool IsOutOfEvents => NetBase == null ? true : !NetBase.ShouldTick;
        public int TicksBehind => NetBase == null ? 0 : NetBase.TicksBehind;
        public bool IsSessionOver => NetBase == null || NetBase.IsStopped;

        public void Close()
        {
            if (NetBase == null) return;
            NetBase.Close();
        }

        public void Update()
        {
            if (NetBase == null) return;
            NetBase.Update();
        }

        /// <summary>
        /// Reads one received frame, or returns null if it cannot be read: a "$type" in it was refused (see
        /// ReplayEventBinder) or is not loaded on this computer, it is not an action at all, or it is a group of
        /// actions that no player could play. <paramref name="problem"/> then says why, naming the type and its
        /// assembly where there is one.
        /// </summary>
        private static ReplayEvent ToEvent(JObject obj, out string problem)
        {
            //Plugin.Log($"Recieving {obj}");
            try
            {
                problem = null;
                ReplayEvent replayEvent = JsonSettings.Deserialize<ReplayEvent>(obj.ToString());
                // Groups are opened one level deep before replay (ReplayService.ReadEventsFromIO): an empty entry or
                // a group inside would fail there, before it changed anything, and a failed replay stops the session.
                // Nobody sends either, so it is dropped here like any other frame that cannot be read.
                if (replayEvent is GroupedEvent group && (group.events == null || group.events.Any(e => e == null || e is GroupedEvent)))
                    problem = "The frame's group of actions holds an empty entry or another group, which cannot be played.";
                else if (replayEvent != null) return replayEvent;
                else problem = "The frame holds no action.";
            }
            catch (Exception ex)
            {
                problem = Describe(ex);
            }
            Plugin.Log("The frame that could not be read: " + obj);
            return null;
        }

        // Newtonsoft names the "$type" it could not create and where it was; the exceptions inside say why.
        private static string Describe(Exception error)
        {
            var messages = new List<string>();
            for (Exception e = error; e != null; e = e.InnerException)
            {
                if (!messages.Contains(e.Message)) messages.Add(e.Message);
            }
            return string.Join(" ", messages);
        }

        /// <summary>
        /// A received frame could not be read; <paramref name="problem"/> says why. Returns true to go on reading
        /// the other frames, or false to drop every frame read with it. Called from ReadEvents, which runs inside a
        /// tick with nothing to catch an exception, so this must not throw.
        /// </summary>
        protected abstract bool HandleUnreadableFrame(string problem);

        public List<ReplayEvent> ReadEvents(int ticksSinceLoad)
        {
            if (NetBase == null) return new List<ReplayEvent>();
            List<ReplayEvent> events = new List<ReplayEvent>();
            foreach (JObject frame in NetBase.ReadEvents(ticksSinceLoad))
            {
                ReplayEvent replayEvent = ToEvent(frame, out string problem);
                if (replayEvent != null) events.Add(replayEvent);
                else if (!HandleUnreadableFrame(problem)) return new List<ReplayEvent>();
            }
            return events;
        }

        public virtual void WriteEvents(params ReplayEvent[] events)
        {
            if (NetBase == null) return;
            foreach (ReplayEvent e in events)
            {
                // TODO: It is silly to convert to JObject here, but not sure if there's
                // a better way to do it.
                NetBase.DoUserInitiatedEvent(JObject.Parse(JsonSettings.Serialize(e)));
            }
        }

        public bool HasEventsForTick(int tick)
        {
            if (NetBase == null) return false;
            return NetBase.HasEventsForTick(tick);
        }
    }
}
