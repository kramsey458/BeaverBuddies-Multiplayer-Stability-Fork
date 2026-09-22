using BeaverBuddies.IO;
using System;
using Timberborn.BaseComponentSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.TemplateSystem;
using static BeaverBuddies.SingletonManager;

namespace BeaverBuddies.Events
{
    public interface IReplayContext
    {
        T GetSingleton<T>();
    }

    /// <summary>
    /// A replayed action names something this game does not have (a building from a mod only another player runs), found
    /// before any of the action was played. A guest that meets one leaves quietly (ReplayService): it can no longer keep
    /// up with the host, but nothing has gone wrong for anyone else.
    /// </summary>
    public class MissingContentException : Exception
    {
        public MissingContentException(string message) : base(message) { }
    }

    public abstract class ReplayEvent : IComparable<ReplayEvent>
    {
        public static readonly string LocalPlayerID = GuidPatcher.RealNewGuid().ToString();

        public int ticksSinceLoad;
        public int? randomS0Before;
        // All four words of Unity's random state before the host played this event, hashed (see DesyncCheck).
        public int? randomStateHashBefore;

        public string type => GetType().Name;

        public int CompareTo(ReplayEvent other)
        {
            if (other == null)
                return 1;
            //return timeInFixedSecs.CompareTo(other.timeInFixedSecs);
            return ticksSinceLoad.CompareTo(other.ticksSinceLoad);
        }

        public abstract void Replay(IReplayContext context);

        /// <summary>
        /// Whether playing this changes what a save would hold. A player who joins is sent the save the host loaded
        /// and only what is played after they connected, so once an action that changes the game has been played
        /// before the first tick, nobody else can join (see ReplayService.CloseJoiningIfGameChanged). False only for
        /// events a player joining later can do without: heartbeats, the speed, the host's greeting (sent to every
        /// guest as it joins), desync reports and traces, pings. Any other event, another mod's included, keeps true.
        /// A method, not a property: properties are written into the event's JSON, and the JSON is hashed.
        /// </summary>
        public virtual bool ChangesGame() => true;

        public override string ToString()
        {
            return type;
        }

        public virtual string ToActionString()
        {
            return $"Doing: {type}";
        }

        public static EntityComponent GetEntityComponent(IReplayContext context, string entityID)
        {
            if (!Guid.TryParse(entityID, out Guid guid))
            {
                Plugin.LogWarning($"Could not parse guid: {entityID}");
                return null;
            }
            var entity = context.GetSingleton<EntityRegistry>().GetEntity(guid);
            if (entity == null)
            {
                Plugin.LogWarning($"Could not find entity: {entityID}");
            }
            return entity;
        }

        public static T GetComponent<T>(IReplayContext context, string entityID)
        {
            var entity = GetEntityComponent(context, entityID);
            if (entity == null) return default;
            var component = entity.GetComponent<T>();
            if (component == null)
            {
                Plugin.LogWarning($"Could not find component {typeof(T)} on entity {entityID}");
            }
            return component;
        }

        public static string GetEntityID(BaseComponent component)
        {
            return component?.GetComponent<EntityComponent>()?.EntityId.ToString();
        }

        protected BuildingSpec GetBuilding(IReplayContext context, string buildingName)
        {
            BuildingSpec result = null;
            // The game throws for a name it does not know. The host refuses a guest's action naming a building the host
            // does not have (ReplayService.NamesMissingBuilding), so only a guest meets one here: the host used a building from
            // a mod this guest does not run. It is the first thing a replay asks, before anything of the action is played,
            // so this guest can leave without harm to anyone (MissingContentException; ReplayService).
            try { result = context.GetSingleton<BuildingService>()?.GetBuildingTemplate(buildingName); }
            catch (ArgumentException) { }
            if (result == null)
                throw new MissingContentException($"The host used the building {buildingName}, which this game does not have " +
                    "(it comes from a mod that is not installed here).");
            return result;
        }

        public static string GetBuildingName(EntitySetup.Builder entitySetupBuilder)
        {
            var spec = entitySetupBuilder.Template.GetSpec<BuildingSpec>();
            return spec?.GetSpec<TemplateSpec>()?.TemplateName;
        }

        public static ReplayService GetReplayServiceIfReady()
        {
            // If we haven't loaded yet, we're not ready
            if (!ReplayService.IsLoaded) return null;

            var replayService = GetSingleton<ReplayService>();
            if (replayService == null || replayService.IsDesynced) return null;
            return replayService;
        }
        

        /// <summary>
        /// Helper method to make overriding recorded actions in game easier.
        /// A prefix that records an action this way (or calls ReplayService.RecordEvent itself) carries
        /// [HarmonyPriority(Priority.First)]. When the local player acts it records the action and skips the game's
        /// method, which then runs while the action is played, on every computer at the same tick. Another mod's
        /// prefix on the same method must run inside that replay, not at the click: ahead of this one it would run on
        /// the acting computer only, and if it returned false Harmony would skip this prefix, so the action would
        /// never be recorded or sent. First also puts this prefix in the same place on every computer, whatever the
        /// load order. A prefix that replaces the game's method instead carries Priority.Last (StabilityTests/README.md).
        /// RuntimeChecks (RecordingPriorityChecks) finds every recording prefix and fails if one is not First.
        /// </summary>
        /// <param name="getEvent">
        /// A function that returns the event to record, or null
        /// if we should skip recording and do the default method behavior.
        /// </param>
        /// <returns>True if the method should use default behavior</returns>
        public static bool DoPrefix(Func<ReplayEvent> getEvent)
        {
            // If we're already replaying events, just let the original method run.
            // This handles nested calls (e.g., Replay() calls Unlock() which triggers this prefix again)
            if (ReplayService.HasReplayFailure) return false;
            if (ReplayService.IsReplayingEvents) return true;
            // The simulation itself calling the method (RunAsSimulation), the same on every computer: not a click.
            if (simulationCalls > 0) return true;

            // If the replay service is not available, just use default behavior
            ReplayService replayService = GetReplayServiceIfReady();
            if (replayService == null) return true;

            // Get the event and if it's null, just use default behavior
            ReplayEvent message = getEvent();
            if (message == null) return true;

            // Optional: Log the message
            Plugin.Log(message.ToActionString());

            // Record the event
            replayService.RecordEvent(message);

            // Return based on the EventIO's desired behavior
            return EventIO.ShouldPlayPatchedEvents;
        }

        // Game code that calls a method players' clicks are recorded from, from inside the simulation itself (a
        // spring-return lever switching itself off in the tick): the call runs at once on every computer, as in single
        // player, instead of each computer taking it for a click of its own and sending it to the host. Only for calls
        // every computer makes at the same point of the same tick; see Fixes/TickTimingFixes. Main thread only.
        private static int simulationCalls;

        /// <summary>Runs <paramref name="action"/> as the simulation's own call: recording prefixes let it through.</summary>
        public static void RunAsSimulation(Action action)
        {
            EnterSimulationCall();
            try { action(); }
            finally { ExitSimulationCall(); }
        }

        public static void EnterSimulationCall() => simulationCalls++;

        public static void ExitSimulationCall() => simulationCalls = Math.Max(0, simulationCalls - 1);

        public static bool DoEntityPrefix(BaseComponent component, Func<string, ReplayEvent> doRecord)
        {
            return DoPrefix(() =>
            {
                string entityID = GetEntityID(component);
                // If this is happening to a non-entity (e.g. prefab),
                // just let the base method handle it
                if (entityID == null) return null;
                var message = doRecord(entityID);
                // Advisory "Editing" notice for other players; presentation only.
                if (message != null && component.HasComponent<Building>())
                    BeaverBuddies.Activity.PlayerActivityService.NotifyLocalEdit(entityID);
                return message;
            });
        }
    }

}
