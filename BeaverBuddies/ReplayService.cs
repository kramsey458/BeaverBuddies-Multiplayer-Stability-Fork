// Define to force game to run a full tick each
// update, rather than amortizing ticks over multiple.
//#define ONE_TICK_PER_UPDATE

using BeaverBuddies.Connect;
using BeaverBuddies.DesyncDetecter;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Reporting;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Timberborn.Automation;
using Timberborn.Autosaving;
using Timberborn.BlockObjectTools;
using Timberborn.Buildings;
using Timberborn.CoreUI;
using Timberborn.DemolishingUI;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSaveRuntimeSystem;
using Timberborn.GameWonderCompletion;
using Timberborn.Options;
using Timberborn.PlantingUI;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using Timberborn.WebNavigation;
using Timberborn.Workshops;
using Timberborn.WorkSystem;
using Timberborn.WorkSystemUI;
using Timberborn.ZiplineSystem;
using static BeaverBuddies.SingletonManager;
using static Timberborn.TickSystem.TickableSingletonService;

namespace BeaverBuddies
{
    public interface IEarlyTickableSingleton : ITickableSingleton
    {
    }

    /**
     * Represents a group of events that should be sent
     * and received together, to ensure all events for a tick
     * are present before they are played.
     */
    class GroupedEvent : ReplayEvent
    {
        public List<ReplayEvent> events;

        public GroupedEvent(List<ReplayEvent> events)
        {
            // Make a copy
            this.events = events.ToList();
        }

        public override void Replay(IReplayContext context)
        {
            throw new NotImplementedException("Do not directly replay grouped events");
        }
    }

    class HeartbeatEvent : ReplayEvent
    {
        public override bool ChangesGame() => false;

        // The host's TEBPatcher hashes when this tick started, compared by every guest (see DesyncCheck).
        public int? entityOrderHash;
        public int? walkerPositionHash;
        /// <summary>
        /// The speed the host runs at while it eases off for a slow guest (HostPacing, FrameRatePacing), for the guests to
        /// follow (CatchUpSpeed.PaceFor); left out at full speed. How fast ticks are worked through, never what is in them.
        /// </summary>
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public float? hostSpeed;

        public override void Replay(IReplayContext context)
        {
            if (EventIO.Get() is ClientEventIO) context.GetSingleton<ReplayService>()?.SetHostPace(hostSpeed);
        }
    }

    public class ReplayService : RegisteredSingleton, IReplayContext, IPostLoadableSingleton, IUpdatableSingleton, IResettableSingleton
    {
        //private readonly TickWathcerService _tickWathcerService;
        private readonly EventBus _eventBus;
        private readonly SpeedManager _speedManager;
        private readonly GameSaver _gameSaver;
        private readonly ISingletonRepository _singletonRepository;
        private readonly TickingService _tickingService;
        private readonly DeterminismService _determinismService;

        private readonly GameSaveHelper gameSaveHelper;

        private List<object> singletons = new();

        // TODO: I believe that this could be a non-static variable
        // set at initialization time, which would prevent accidentally
        // accessing a new game's event IO.
        private EventIO io => EventIO.Get();

        private int __ticksSinceLoad = 0;
        private int ticksSinceLoad 
        { 
            get => __ticksSinceLoad;
            set
            {
                __ticksSinceLoad = value;
                TimeTimePatcher.SetTicksSinceLoaded(value);
                TEBPatcher.StartTick(value);
                DesyncDetecterService.StartTick(value);
            }
        }
        public int TicksSinceLoad => ticksSinceLoad;

        /// <summary>The speed the game is asked to run at: the chosen speed plus the session's boost (0 paused).</summary>
        public float TargetSpeed  { get; private set; } = 0;

        /// <summary>The speed the players picked at the top right (1, 3 or 7 for the game's speed 1, 2 and 3; 0 paused).</summary>
        public float ChosenSpeed { get; private set; } = 0;

        /// <summary>The session's speed boost (SpeedBoost): added to the chosen speed, the same for everyone.</summary>
        public float Boost { get; private set; } = 0;

        // The boost again, readable from any thread: the start message for a joining player is built on the network
        // thread (see InitializeClientEvent.Create). A new session starts at 0.
        private static volatile float sessionBoost;
        public static float SessionBoost => sessionBoost;
        public bool IsDesynced { get; private set; } = false;
        public static bool HasReplayFailure { get; private set; }

        private ConcurrentQueue<ReplayEvent> eventsToSend = new ConcurrentQueue<ReplayEvent>();
        private ConcurrentQueue<ReplayEvent> eventsToPlay = new ConcurrentQueue<ReplayEvent>();

        /// <summary>
        /// Returns true if the ReplayService is ready for the game to
        /// start a new a tick, and false if not (e.g. because the client
        /// has not yet received a heartbeat from the server for the next tick).
        /// </summary>
        public bool IsReadyToStartTick {
            get
            {
                // The client shouldn't tick until the server has sent a heartbeat
                // Check the *next* tick, since current tick has already happened
                return !(io is ClientEventIO && !io.HasEventsForTick(TicksSinceLoad + 1));
            }
        }

        public static bool IsLoaded { get; private set; } = false;
        private bool isReset = false;

        private bool CanAct => io != null && !isReset && !IsDesynced;

        public static bool IsReplayingEvents { get; private set; } = false;

        public string ServerMapName => GetSingleton<MapNameService>().Name;

        public void Reset()
        {
            Plugin.Log("Resetting Replay Service...");
            IsLoaded = false;
            HasReplayFailure = false;
            IsReplayingEvents = false;
            isReset = true;
        }

        public ReplayService(
            EventBus eventBus,
            SpeedManager speedManager,
            GameSaver gameSaver,
            ISingletonRepository singletonRepository,
            TickingService tickingService,
            DeterminismService determinismService,
            BlockObjectPlacerService blockObjectPlacerService,
            BuildingService buildingService,
            PlantingSelectionService plantingSelectionService,
            TreeCuttingArea treeCuttingArea,
            EntityRegistry entityRegistry,
            EntityService entityService,
            RecipeSpecService recipeSpecificationService,
            DemolishableSelectionTool demolishableSelectionTool,
            DemolishableUnselectionTool demolishableUnselectionTool,
            BuildingUnlockingService buildingUnlockingService,
            WorkingHoursManager workingHoursManager,
            WorkingHoursPanel workingHoursPanel,
            WorkplaceUnlockingService workplaceUnlockingService,
            IOptionsBox optionsBox,
            DialogBoxShower dialogBoxShower,
            UrlOpener urlOpener,
            RehostingService rehostingService,
            ReportingService reportingService,
            GameSaveRepository gameSaveRepository,
            MapNameService mapNameService,
            Autosaver autosaver,
            ZiplineConnectionService ziplineConnectionService,
            Settings settings,
            TemplateInstantiator templateInstantiator,
            AutomationResetter automationResetter
        )
        {
            sessionBoost = 0;
            //_tickWathcerService = AddSingleton(tickWathcerService);
            _eventBus = AddSingleton(eventBus);
            _speedManager = AddSingleton(speedManager);
            _gameSaver = AddSingleton(gameSaver);
            _singletonRepository = AddSingleton(singletonRepository);
            _tickingService = AddSingleton(tickingService);
            _determinismService = AddSingleton(determinismService);
            AddSingleton(blockObjectPlacerService);
            AddSingleton(buildingService);
            AddSingleton(plantingSelectionService);
            AddSingleton(treeCuttingArea);
            AddSingleton(entityRegistry);
            AddSingleton(entityService);
            AddSingleton(recipeSpecificationService);
            AddSingleton(demolishableSelectionTool);
            AddSingleton(demolishableUnselectionTool);
            AddSingleton(buildingUnlockingService);
            AddSingleton(workingHoursManager);
            AddSingleton(workingHoursPanel);
            AddSingleton(workplaceUnlockingService);
            AddSingleton(optionsBox);
            AddSingleton(dialogBoxShower);
            AddSingleton(urlOpener);
            AddSingleton(rehostingService);
            AddSingleton(reportingService);
            AddSingleton(gameSaveRepository);
            AddSingleton(mapNameService);
            AddSingleton(autosaver);
            AddSingleton(ziplineConnectionService);
            AddSingleton(settings);
            AddSingleton(templateInstantiator);
            AddSingleton(automationResetter);

            AddSingleton(this);

            _eventBus.Register(this);

            gameSaveHelper = new GameSaveHelper(gameSaver);

            _tickingService.replayService = this;
        }

        public void SetTicksSinceLoad(int ticks)
        {
            Plugin.Log($"Setting ticks since load to: {ticks}");
            ticksSinceLoad = ticks;
        }

        public void PostLoad()
        {
            Plugin.Log("PostLoad");
            _determinismService.UnityThread = Thread.CurrentThread;
        }

        private T AddSingleton<T>(T singleton)
        {
            singletons.Add(singleton);
            return singleton;
        }

        public T GetSingleton<T>()
        {
            foreach (object singleton in singletons)
            {
                if (singleton is T)
                    return (T)singleton;
            }
            // This doesn't work for some singletons, like GetSingletons<T>(),
            // so we still have to add them manually.
            var result = _singletonRepository.GetSingletons<T>().FirstOrDefault();
            Plugin.Log($"Searching for unregistered singleton {typeof(T)}; found = {result != null}");
            return result;
        }

        public void RecordEvent(ReplayEvent replayEvent)
        {
            // During a replay, we save things manually, only if they're
            // successful.
            if (IsReplayingEvents) return;
            if (!IsLoaded) return;

            if (Settings.Debug && Settings.VerboseLogging)
                Plugin.Log($"RecordEvent: {JsonSettings.Serialize(replayEvent)}");

            UserEventBehavior behavior = UserEventBehavior.Send;
            EventIO io = EventIO.Get();
            if (io != null)
            {
                behavior = io.UserEventBehavior;
            }
            if (behavior == UserEventBehavior.QueuePlay)
            {
                eventsToPlay.Enqueue(replayEvent);
            }
            else
            {
                EnqueueEventForSending(replayEvent);
            }
        }

        private List<ReplayEvent> ReadEventsFromIO(int tick)
        {
            List<ReplayEvent> eventsToReplay = io.ReadEvents(tick);
            // Spread grouped events into a flat list because we need to
            // replay each individually (since we only record them if successful).
            eventsToReplay = eventsToReplay
                .SelectMany(e => e is GroupedEvent grouped ? grouped.events.ToArray() : new ReplayEvent[] { e })
                .ToList();
            return eventsToReplay;
        }

        private void ReplayEvents()
        {
            if (_tickingService.NextBucket != 0)
            {
                Plugin.LogWarning($"Warning, replaying events when bucket != 0: {_tickingService.NextBucket}");
            }

            // Start with any events received from connected users
            List<ReplayEvent> eventsToReplay = ReadEventsFromIO(TicksSinceLoad);
            // Then add any from this user that have been deferred
            while (eventsToPlay.TryDequeue(out ReplayEvent replayEvent))
            {
                replayEvent.ticksSinceLoad = ticksSinceLoad;
                eventsToReplay.Add(replayEvent);
            }

            int currentTick = ticksSinceLoad;
            ReplayExecution.Run(eventsToReplay, replayEvent =>
            {
                if (HasReplayFailure || IsDesynced || EventIO.IsNull) return false;
                int eventTime = replayEvent.ticksSinceLoad;
                if (eventTime > currentTick)
                    return false;
                if (eventTime < currentTick)
                {
                    Plugin.LogWarning($"Event past time: {eventTime} < {currentTick}");
                }
                //Plugin.Log($"Replaying event [{replayEvent.ticksSinceLoad}]: {replayEvent.type}");

                // The host: a guest's action naming a building this game does not have (from a mod the guest runs and the
                // host does not). Played, it threw here and stopped the session for everyone. Refused, no computer plays
                // it and the game goes on.
                if (io is ServerEventIO && NamesMissingBuilding(replayEvent, out string missingBuilding))
                {
                    Plugin.LogWarning($"Refused {replayEvent.type}: this game has no building {missingBuilding} " +
                        "(it comes from a mod a guest runs and the host does not)");
                    return true;
                }

                // If this event was played (e.g. on the server) and recorded a 
                // random state, make sure we're in the same state.
                // Keep this check independent of detailed logging preferences.
                string mismatch = FindMismatchWithHost(replayEvent, out bool stops);
                if (mismatch != null && stops)
                {
                    Plugin.LogWarning(mismatch);
                    HandleDesync(mismatch);
                    return false;
                }
                // Entities or walkers differ while the random state agrees: logged once, the game goes on.
                if (mismatch != null && TEBPatcher.FirstTickDifference())
                {
                    Plugin.LogWarning(mismatch + ". Logged only, the game goes on: the random state still matches the host's. " +
                        "If a desync follows, this line says when the games first differed.");
                }
                // Only broadcast successful events from an active session.
                RecordRandomState(replayEvent);
                replayEvent.Replay(this);
                CloseJoiningIfGameChanged(io, currentTick, replayEvent);
                if (CanAct && !EventIO.SkipRecording)
                {
                    EnqueueEventForSending(replayEvent);
                }
                return !IsDesynced && !HasReplayFailure && !EventIO.IsNull;
            }, (replayEvent, error) =>
            {
                Plugin.LogError($"Failed to replay event {replayEvent?.type}: {error}");
                // A guest missing a building the host used (from a mod the guest does not run), found before anything of the
                // action was played: only this guest leaves, and the host and the others play on. It used to stop the
                // whole session.
                if (error is MissingContentException missing && io is ClientEventIO)
                    AbortReplay(missing.Message, leaveQuietly: true);
                else
                    AbortReplay("A multiplayer action could not be completed.");
            }, active => IsReplayingEvents = active, IsReplayingEvents);
        }

        /// <summary>
        /// A player who joins loads the save the host started from (ServerEventIO serves the bytes it loaded) and is
        /// sent only what is played after it connected. So the first action that changes the game, played while the
        /// host waits at tick 0, closes joining, as the first tick does (DoTick). Called after the action is played
        /// and before it is queued to be sent: a guest admitted before this gets it, and one still joining is refused
        /// with the host's reason (TimberServer.StartQueuing checks again under the lock every broadcast takes).
        /// </summary>
        internal static void CloseJoiningIfGameChanged(EventIO io, int tick, ReplayEvent replayEvent)
        {
            if (io is ServerEventIO server && tick == 0 && replayEvent.ChangesGame())
            {
                server.StopAcceptingClients(gameChanged: true);
            }
        }

        /// <summary>
        /// Whether <paramref name="replayEvent"/> places or unlocks a building this game does not have. The game throws
        /// for a building name it does not know.
        /// </summary>
        private bool NamesMissingBuilding(ReplayEvent replayEvent, out string building)
        {
            building = replayEvent is BuildingPlacedEvent placing ? placing.prefabName
                : replayEvent is Events.BuildingUnlockedEvent unlocking ? unlocking.buildingName : null;
            if (building == null) return false;
            BuildingService buildings = GetSingleton<BuildingService>();
            if (buildings == null) return false;
            try { return buildings.GetBuildingTemplate(building) == null; }
            catch (Exception) { return true; }
        }

        public void AbortReplay(string reason) => AbortReplay(reason, leaveQuietly: false);

        private const string FailedActionAdvice = "Multiplayer has stopped because this action may have changed only part of the game state. "
            + "Return to the main menu and reload a known-good save before rehosting. Do not overwrite your good save with this session.";
        private const string LeftQuietlyAdvice = "Nothing of that action was played here, so no save is harmed, but this game can no longer "
            + "keep up with the host's: you have left the game, and the host and the other players play on. To play together again, "
            + "install the mod named above (or the host stops using it), then the host saves and hosts again, and you join.";

        /// <summary>
        /// Stops multiplayer here: an action failed partway, or another player said one did. With
        /// <paramref name="leaveQuietly"/>, a guest that met a building from a mod it does not run (MissingContentException):
        /// nothing of the action was played here and nothing went wrong anywhere else, so this guest only leaves (the
        /// others are not told to stop) and is told how to join again.
        /// </summary>
        public void AbortReplay(string reason, bool leaveQuietly)
        {
            if (HasReplayFailure) return;
            HasReplayFailure = true;
            IsDesynced = true;
            TargetSpeed = 0;
            _tickingService.ShouldInterruptTicking = true;
            eventsToPlay.Clear();
            eventsToSend.Clear();
            try
            {
                if (io is ServerEventIO server) server.NetBase?.AbortSession(reason);
                else if (io is ClientEventIO client && leaveQuietly) client.NetBase?.Close();
                else if (io is ClientEventIO other) other.NetBase?.AbortSession(reason);
            }
            catch (Exception error) { Plugin.LogError(error.ToString()); }
            finally
            {
                EventIO.Reset();
                SpeedChangePatcher.SetSpeedSilentlyNow(_speedManager, 0);
            }
            // Like a desync: input held when the dialog appears must not carry over once it is closed.
            GetSingleton<BeaverBuddies.Fixes.MultiplayerInputRecovery>()?.RequestReset();
            GetSingleton<DialogBoxShower>().Create()
                .SetMessage(reason + "\n\n" + (leaveQuietly ? LeftQuietlyAdvice : FailedActionAdvice))
                .SetDefaultCancelButton().Show();
        }

        /// <summary>
        /// The network session is over while the game is still running: the connection dropped, or a host gave up
        /// rehosting. Multiplayer is left the way a desync leaves it, so that what the player does is applied here
        /// instead of being queued for a session that is gone. Without that every patched action, the menu included,
        /// goes nowhere and the game stays paused. The game is left paused. <paramref name="message"/> is shown to the
        /// player; null says nothing (the host chose to give up).
        /// </summary>
        public void EndSession(string message)
        {
            // A desync or a failed action ended it already, and said so.
            if (IsDesynced) return;
            Plugin.Log("Ending the multiplayer session: " + (message ?? "no message"));
            IsDesynced = true;
            TargetSpeed = 0;
            eventsToPlay.Clear();
            eventsToSend.Clear();
            EventIO.Reset();
            SpeedChangePatcher.SetSpeedSilentlyNow(_speedManager, 0);
            GetSingleton<BeaverBuddies.Fixes.MultiplayerInputRecovery>()?.RequestReset();
            if (message == null) return;
            try
            {
                GetSingleton<DialogBoxShower>().Create().SetMessage(message).Show();
            }
            catch (Exception error)
            {
                // Losing the message is better than losing the game.
                Plugin.LogError("Could not show the multiplayer message: " + error);
            }
        }

        public void HandleDesync()
        {
            HandleDesync(null);
        }

        /// <param name="reason">
        /// What the always-on check found (see DesyncCheck). Without detailed logging there is no trace, so this is
        /// sent as the trace instead: the host's log and any report then say what differed, not only this guest's log.
        /// </param>
        public void HandleDesync(string reason)
        {
            if (IsDesynced) return;

            string trace = DesyncDetecterService.GetLastDesyncTrace();
            if (string.IsNullOrEmpty(trace) && reason != null) trace = reason;
            ClientDesyncedEvent e = new ClientDesyncedEvent()
            {
                // The same ID as DesyncDetecterService.GetLastDesyncID when the trace is that one.
                desyncID = ReportingService.GetStringHash(trace),
                desyncTrace = trace,
            };
            // Set IsDesynced to true so event play instead of sending
            // to the host, allowing the Client to continue play.
            IsDesynced = true;
            // Don't use EnqueueEventForSending because it shouldn't
            // have a random state set.
            eventsToSend.Enqueue(e);
            e.Replay(this);
            // Send events immediately to get this event out before resetting
            // the EventIO
            // TODO: This only works because sending events is currently a synchronous
            // operation, and it really shouldn't be, so this is a short-term fix!
            if (SendEvents()) FlushSteam();
            // Pause
            SpeedChangePatcher.SetSpeedSilentlyNow(_speedManager, 0);
            EventIO.Reset();
        }

        /**
         * Readies and event for sending to connected players.
         * Adds the randomS0 if the event will be played, but assumed
         * this is called *before* the event is played. If this is 
         * called after an event is played, the randomS0 should already
         * be set (it will not be overwritten).
         */
        private void EnqueueEventForSending(ReplayEvent replayEvent)
        {
            // Only set the random state if this recoded event is
            // actually going to be played, or if it's a heartbeat.
            // But don't overwrite the random state if it's already set.
            if (!replayEvent.randomS0Before.HasValue &&
                (EventIO.ShouldPlayPatchedEvents || replayEvent is HeartbeatEvent))
            {
                RecordRandomState(replayEvent);
                //Plugin.Log($"Recording event s0: {replayEvent.randomS0Before}");
            }
            // The heartbeat starts each tick, so it also says what the host's entities looked like when the
            // previous tick finished. Each guest compares that at the start of the same tick.
            if (replayEvent is HeartbeatEvent heartbeat && !heartbeat.entityOrderHash.HasValue)
            {
                heartbeat.entityOrderHash = TEBPatcher.EntityUpdateHash;
                heartbeat.walkerPositionHash = TEBPatcher.PositionHash;
            }
            eventsToSend.Enqueue(replayEvent);
        }

        private static void RecordRandomState(ReplayEvent replayEvent)
        {
            UnityEngine.Random.State state = UnityEngine.Random.state;
            replayEvent.randomS0Before = state.s0;
            replayEvent.randomStateHashBefore = DesyncCheck.RandomStateHash(state.s0, state.s1, state.s2, state.s3);
        }

        /// <summary>
        /// The always-on desync check (see DesyncCheck): null when this game is where the host's was when it
        /// played this event, otherwise what differs. <paramref name="stops"/> is true when the random state differs,
        /// which stops the session; an entity or walker difference alone is only logged.
        /// </summary>
        private static string FindMismatchWithHost(ReplayEvent replayEvent, out bool stops)
        {
            stops = false;
            HeartbeatEvent heartbeat = replayEvent as HeartbeatEvent;
            // What a guest sends reaches the host with nothing to compare, and the host checks nothing.
            if (replayEvent.randomS0Before == null && replayEvent.randomStateHashBefore == null &&
                heartbeat?.entityOrderHash == null && heartbeat?.walkerPositionHash == null)
            {
                return null;
            }
            UnityEngine.Random.State random = UnityEngine.Random.state;
            var local = new GameState
            {
                S0 = random.s0, S1 = random.s1, S2 = random.s2, S3 = random.s3,
                EntityOrder = TEBPatcher.EntityUpdateHash,
                WalkerPositions = TEBPatcher.PositionHash,
            };
            stops = DesyncCheck.RandomMismatch(replayEvent.randomS0Before, replayEvent.randomStateHashBefore, local) != null;
            return DesyncCheck.Mismatch(replayEvent.randomS0Before, replayEvent.randomStateHashBefore,
                heartbeat?.entityOrderHash, heartbeat?.walkerPositionHash, local);
        }

        /// <summary>Sends what is queued as one group. True if anything was sent.</summary>
        private bool SendEvents()
        {
            if (EventIO.IsNull) return false;
            // Called every frame for a guest, so skip the allocations when there is nothing to send.
            if (eventsToSend.IsEmpty) return false;
            List<ReplayEvent> events = new List<ReplayEvent>();
            while (eventsToSend.TryDequeue(out ReplayEvent replayEvent))
            {
                replayEvent.ticksSinceLoad = ticksSinceLoad;
                events.Add(replayEvent);
            }
            // Don't send an empty list to save bandwidth.
            if (events.Count == 0) return false;
            GroupedEvent group = new GroupedEvent(events);
            group.ticksSinceLoad = ticksSinceLoad;
            EventIO.Get().WriteEvents(group);
            return true;
        }

        /// <summary>
        /// Hands what was just sent to Steam now. Steam moves data only when the game thread pumps it: once a frame before
        /// anything else, and between buckets while ticking. What is sent while paused, or by a guest waiting at the start
        /// of a tick (no buckets run), otherwise waited for the next frame's pump, a frame each way. Direct connections
        /// send at once and are not affected.
        /// </summary>
        private static void FlushSteam() => Steam.SteamNet.PumpBetweenTicks(force: true);

        /**
         * Replays any pending events from the user or connected users
         * and then sends successful/pending events to connected users.
         * This should only be called if the game is paused at the end of
         * a tick or right at the start of a tick, so that events always
         * are recorded and replayed at the exact same time in the update loop.
         */
        private bool DoTickIO()
        {
            ReplayEvents();
            return SendEvents();
        }

        private void Initialize()
        {
            // Start tick at 0
            DesyncDetecterService.StartTick(ticksSinceLoad);

            IsLoaded = true;
        }

        // TODO: Find a better callback way of waiting until initial game
        // loading and randomization is done.
        private int waitUpdates = 2;

        public void UpdateSingleton()
        {
            if (!CanAct) return;
            ReportFrameRate();
            if (waitUpdates > 0)
            {
                waitUpdates--;
                return;
            }
            if (waitUpdates == 0)
            {
                Initialize();
                waitUpdates = -1;
            }
            io.Update();
            if (!CanAct) return;
            // A session that ended without anyone saying so: a host that cancelled its rehost, or a guest whose
            // connection dropped while this game was still loading. Only a guest is told; a host chose it.
            if (io.IsSessionOver)
            {
                EndSession(io is ClientEventIO ? SessionEndMessages.ConnectionLost(null) : null);
                return;
            }
            // Only replay events on Update if we're paused by the user.
            // Also only send events if paused, so the client doesn't play
            // then before the end of the tick.
            if (_speedManager.CurrentSpeed == 0 && TargetSpeed == 0)
            {
                if (DoTickIO()) FlushSteam();
            }
            else if (io is ClientEventIO)
            {
                // A guest's own actions are only recorded here. They are never played locally
                // and the guest does not hash them: the host decides which tick they run on when
                // it replays them and sends them back. So they can leave as soon as they are made
                // instead of waiting for the next tick boundary, which saves about half a tick
                // of input delay on average.
                if (SendEvents()) FlushSteam();
            }
            UpdateSpeed();
        }

        // A guest: the host's pace while it eases off for a slow guest, from its last heartbeat (CatchUpSpeed.PaceFor).
        private float? hostPace;

        /// <summary>A guest: the host's pace, as its heartbeat says (null at full speed).</summary>
        public void SetHostPace(float? pace) => hostPace = pace;

        /// <summary>
        /// The host: its own pace while it eases off for a slow guest (HostPacing, FrameRatePacing), for the guests to
        /// follow; null at full speed, and while it holds still (a guest behind it then catches up).
        /// </summary>
        private float? PaceForGuests()
        {
            float eased = hostPacing.Apply(TargetSpeed, frameRatePacing.Percent);
            return eased > 0 && eased < TargetSpeed ? eased : (float?)null;
        }

        public void SetTargetSpeed(float speed)
        {
            TargetSpeed = speed;
            UpdateSpeed();
        }

        /// <summary>The players picked a speed (a SpeedSetEvent): the game runs at it plus the boost.</summary>
        public void SetChosenSpeed(float speed)
        {
            ChosenSpeed = speed;
            SetTargetSpeed(SpeedBoost.Apply(speed, Boost));
        }

        /// <summary>
        /// The session's boost changed (a SpeedBoostEvent, or the start message as a player joins): the chosen speed
        /// stays, the game's speed follows. A paused game (the start message always finds one) is left alone.
        /// </summary>
        public void SetBoost(float boost)
        {
            Boost = SpeedBoost.Clamp(boost);
            sessionBoost = Boost;
            float target = SpeedBoost.Apply(ChosenSpeed, Boost);
            if (target != TargetSpeed) SetTargetSpeed(target);
        }

        private readonly HostPacing hostPacing = new HostPacing();
        private readonly System.Diagnostics.Stopwatch hostPacingClock = System.Diagnostics.Stopwatch.StartNew();
        private long nextHostPacingSampleMs;

        /// <summary>How much of the chosen speed the host is running at, in percent. 100 unless it is easing off for a guest.</summary>
        public int HostPacingPercent => hostPacing.Percent;

        /// <summary>True while the host stands still because a guest is very far behind.</summary>
        public bool HostPacingHolding => hostPacing.IsHolding;

        private readonly FrameRatePacing frameRatePacing = new FrameRatePacing();
        private readonly FrameRateMeter frameRateMeter = new FrameRateMeter();

        /// <summary>Percent of the chosen speed the host runs at because of a guest's frame rate. 100 unless the host chose a floor and a guest is below it.</summary>
        public int FrameRatePacingPercent => frameRatePacing.Percent;

        // A guest tells the host its frame rate with each reply to the host's ping probe. Nothing is reported while
        // the window is in the background, where the system throttles it and the figure says nothing about the computer.
        private void ReportFrameRate()
        {
            if (!(io is ClientEventIO guest) || guest.NetBase == null) return;
            frameRateMeter.Frame(UnityEngine.Time.realtimeSinceStartupAsDouble);
            guest.NetBase.ReportedFps = UnityEngine.Application.isFocused ? frameRateMeter.Fps : 0;
        }

        // Guests report their tick about once a second, so sampling more often would count the same report twice.
        private void SampleHostPacing(ServerEventIO host)
        {
            long now = hostPacingClock.ElapsedMilliseconds;
            if (now < nextHostPacingSampleMs) return;
            nextHostPacingSampleMs = now + TimberNet.TimberServer.StatusIntervalMs;
            int before = hostPacing.Percent;
            bool wasHolding = hostPacing.IsHolding;
            hostPacing.Sample(host.NetBase?.WorstGuestTicksBehind, TargetSpeed > 0, TargetSpeed);
            if (hostPacing.IsHolding != wasHolding)
            {
                Plugin.Log(hostPacing.IsHolding
                    ? $"Host pacing: waiting for a guest that is {host.NetBase?.WorstGuestTicksBehind} ticks behind"
                    : "Host pacing: the guest has caught up, carrying on");
            }
            if (hostPacing.Percent != before)
            {
                Plugin.Log($"Host pacing: now {hostPacing.Percent}% of the chosen speed " +
                           $"(slowest guest is {host.NetBase?.WorstGuestTicksBehind} ticks behind)");
            }

            // Same once-a-second reports, a different question: is a guest keeping up in ticks but drawing few frames?
            int fpsBefore = frameRatePacing.Percent;
            int floor = Settings.GuestFpsFloorValue;
            frameRatePacing.Sample(floor, host.NetBase?.WorstGuestFps, TargetSpeed > 1 && !hostPacing.IsHolding);
            if (frameRatePacing.Percent != fpsBefore)
            {
                Plugin.Log($"Host pacing: now {frameRatePacing.Percent}% of the chosen speed for guest frame rate " +
                           $"(slowest guest draws {host.NetBase?.WorstGuestFps?.ToString() ?? "?"} fps, " +
                           $"middle of its last five reports {frameRatePacing.SmoothedFps?.ToString() ?? "?"}, floor {floor})");
            }
        }

        private void UpdateSpeed()
        {
            if (EventIO.IsNull) return;

            if (io.IsOutOfEvents)
            {
                // Also pause the game (silently) if we're out of events
                if (_speedManager.CurrentSpeed != 0)
                {
                    SpeedChangePatcher.SetSpeedSilentlyNow(_speedManager, 0);
                }
                // And return early
                return;
            }

            // If we're not out of ticks to process, speed up while we're behind.
            // A guest works through ticks at the host's pace while the host eases off (CatchUpSpeed.PaceFor).
            float pace = io is ClientEventIO ? CatchUpSpeed.PaceFor(TargetSpeed, hostPace) : TargetSpeed;
            float targetSpeed = CatchUpSpeed.For(pace, io.TicksBehind, _speedManager.CurrentSpeed);

            // The host is never behind. It eases off instead, and only when a guest cannot keep up.
            if (io is ServerEventIO host)
            {
                SampleHostPacing(host);
                targetSpeed = hostPacing.Apply(targetSpeed, frameRatePacing.Percent);
            }

            if (_speedManager.CurrentSpeed != targetSpeed)
            {
                //Plugin.Log($"Setting speed to target speed: {targetSpeed}");
                SpeedChangePatcher.SetSpeedSilentlyNow(_speedManager, targetSpeed);
            }
        }

        // This will be called at the very begining of a tick before
        // anything else has happened, and after everything from the prior
        // tick (including parallel things) has finished.
        public void DoTick()
        {
            if (!CanAct) return;

            if (Settings.Debug && io.ShouldSendHeartbeat)
            {
                // Before incrementing the tick (which creates a new blank trace),
                // capture any unsent traces and send them.
                // Note: this will capture traces for prior ticks, but be sent with
                // an event at the start of the *upcoming* tick.
                foreach (var e in DesyncDetecterService.CreateReplayEventsAndClear())
                {
                    EnqueueEventForSending(e);
                }
            }

            ticksSinceLoad++;

            if (io.ShouldSendHeartbeat)
            {
                // Add a heartbeat if needed to make sure all ticks have
                // at least 1 event, so the clients know we're ticking.
                EnqueueEventForSending(new HeartbeatEvent { hostSpeed = PaceForGuests() });
            }
            // Replay and send events at the change of a tick always.
            // For the server, sending events allows clients to keep playing.
            DoTickIO();

            // Remember DoTickIO can set EventIO to null

            // Log from IO
            io?.Update();

            // IO Complete for Tick
            if (Settings.Debug && Settings.VerboseLogging)
                Plugin.Log($"Tick {ticksSinceLoad:D5} IO done; " +
                $"Order hash: {TEBPatcher.EntityUpdateHash:X8}; " +
                $"Move hash: {TEBPatcher.PositionHash:X8}; " +
                $"Random s0: {UnityEngine.Random.state.s0:X8}");

            if (ticksSinceLoad % 20 == 0)
            {
                //gameSaveHelper.LogStateCheck(ticksSinceLoad);
            }

            // Update speed and pause if needed for the new tick.
            UpdateSpeed();

            if (io is ServerEventIO && ticksSinceLoad == 1)
            {
                ((ServerEventIO)io).StopAcceptingClients();
            }
        }

        public void FinishFullTickIfNeededAndThen(Action action)
        {
            // Paused by the players, the game stands at the start of a tick with nothing of it run yet (a pause is played
            // there), so it can be saved at once. Standing still for another reason (a guest waiting for the host's word,
            // the host easing off for a slow guest) can be in the middle of a tick: that tick is finished first, as when
            // running. Until 1.1.14 any stop saved at once, so a save could hold half a tick.
            if (TargetSpeed == 0)
            {
                action();
                return;
            }
            // If we're not paused, we need to wait until the end of the tick
            _tickingService.FinishFullTickAndThen(action);
        }
    }

    [HarmonyPatch(typeof(TickableSingletonService), nameof(TickableSingletonService.Load))]
    static class TickableSingletonServicePatcher
    {
        static void Postfix(TickableSingletonService __instance)
        {
            // Ensure late singletons come first
            // Create a new list, since the variable is immutable
            var tickableSingletons = new List<MeteredSingleton>(__instance._tickableSingletons);
            var earlySingletons = tickableSingletons
                .Where(s => s._tickableSingleton is IEarlyTickableSingleton).ToList();
            foreach ( var earlySingleton in earlySingletons)
            {
                tickableSingletons.Remove(earlySingleton);
            }
            tickableSingletons.InsertRange(0, earlySingletons);
            __instance._tickableSingletons = tickableSingletons.ToImmutableArray();
        }
    }

    public class TickingService : RegisteredSingleton
    {
        /// <summary>
        /// If true, the TickingService will stop as soon as possible (interrupting
        /// a normal update, but finishing it's current bucket), but it will then resume
        /// on the following update.
        /// </summary>
        public bool ShouldInterruptTicking { get; set; } = false;
        public bool ShouldCompleteFullTick { get; private set; } = false;

        public bool HasTickedReplayService { get; private set; } = false;

        public int NextBucket { get; private set; } = 0;

        // The last tick a "not ready to tick" warning was logged for.
        private int lastNotReadyWarningTick = -1;

        public ReplayService replayService { get; set; }

        // The game's bucket service, as TickBuckets last saw it: what a save waits on (FinishParallelTickBeforeSaving).
        private TickableBucketService bucketService;

        // The game's ticker, which turns each frame's time into buckets: an interruption gives it back the buckets it
        // left unticked (GiveBackBuckets).
        private readonly Ticker ticker;

        public TickingService(Ticker ticker)
        {
            this.ticker = ticker;
        }

        // Should be ok non-concurrent - for now only main thread call this
        private List<Action> onCompletedFullTick = new List<Action>();

        public void FinishFullTick()
        {
            ShouldCompleteFullTick = true;
        }

        public void FinishFullTickAndThen(Action value)
        {
            onCompletedFullTick.Add(value);
            ShouldCompleteFullTick = true;
        }

        internal void OnTickingCompleted()
        {
            if (ReplayService.HasReplayFailure)
            {
                // Never run a deferred save/rehost callback from an aborted tick.
                onCompletedFullTick.Clear();
                ShouldCompleteFullTick = false;
                return;
            }
            // Interruptions are always temporary and get reset at the end of
            // each ticking update
            ShouldInterruptTicking = false;
            if (!ShouldCompleteFullTick) return;
            // An interruption (a deletion, see EntityDeletionEndsFramePatcher) stopped this frame short of the tick's end:
            // what waits for the end of the tick (a save) waits for the next frame, which carries on to it.
            if (bucketService != null && bucketService._nextBucketIndex != 0) return;
            if (onCompletedFullTick.Count > 0) FinishParallelTickBeforeSaving();
            Plugin.Log($"Finished full tick; calling {onCompletedFullTick.Count} callbacks");
            foreach (var action in onCompletedFullTick)
            {
                action();
            }
            onCompletedFullTick.Clear();
            ShouldCompleteFullTick = false;
        }

        /// <summary>
        /// Before a deferred save (or rehost) runs at the end of a tick: the water and soil simulations started at this
        /// tick's first bucket may still be running on their own threads. The game's own save makes them finish
        /// (TickableBucketService.FinishFullTick); a co-op save skips that, as it also lets their listeners catch up
        /// early on this computer alone (TickableBucketService_FinishFullTick_Patch), and so it read their arrays while
        /// they were being written (TickOnlyArrayServiceAllowEditPatch let it). Waiting for them here changes nothing
        /// else: every computer waits for them at the start of the next tick anyway, before anything there is played,
        /// and the water sources held for that moment (LateTickableBuffer) run with nothing in between that reads them.
        /// </summary>
        private void FinishParallelTickBeforeSaving()
        {
            if (bucketService == null) return;
            try
            {
                var singletons = (TickableSingletonService)bucketService._tickableSingletonService;
                if (!singletons.ParalleTicklIsFinished) singletons.FinishParallelTick();
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Could not wait for the water simulation before saving: " + error.Message);
            }
        }

        private bool ShouldTick(TickableBucketService __instance, int numberOfBucketsToTick)
        {

            // Don't tick if we've been interrupted by a forced pause
            if (ReplayService.HasReplayFailure || ShouldInterruptTicking) return false;

            // Don't tick if we've set the game speed to 0 (paused)
            if (replayService.TargetSpeed == 0) return false;

            if (IsAtStartOfTick(__instance) && !HasTickedReplayService)
            {
                // Don't start a brand new tick until the ReplayService
                // is ready. Note that this is only a reason to stop if
                // we're at the *very start* of a new tick - otherwise
                // it would be overly conservative.
                if (!replayService.IsReadyToStartTick)
                {
                    // In theory the game should be paused to prevent this, but some logs
                    // suggest the client can get ahead of the server, which would
                    // trigger this warning (and now prevent the client's tick)
                    int tick = replayService.TicksSinceLoad;
                    // This runs every time the game asks to tick, which can be every frame while a
                    // caught-up guest waits for the host, so log it once per tick.
                    if (tick > 0 && tick != lastNotReadyWarningTick)
                    {
                        lastNotReadyWarningTick = tick;
                        Plugin.LogWarning($"Client trying to tick before receiving " +
                            $"Heartbeat at tick: {tick}");
                    }
                    return false;
                }
            }

            // If we need to complete a full tick, make sure to go exactly until
            // the end of this tick, to ensure an update follows immediately.
            if (ShouldCompleteFullTick)
            {
                return __instance._nextBucketIndex != 0;
            }
            return numberOfBucketsToTick > 0;
        }

        public static bool IsAtStartOfTick(TickableBucketService __instance)
        {
            // For Update 7, index is 0 for singleton ticking
            return __instance._nextBucketIndex == 0;
        }

        private bool TickReplayServiceOrNextBucket(TickableBucketService __instance)
        {
            if (IsAtStartOfTick(__instance))
            {
                // If we're at the start of a tick, and we haven't yet
                // ticked the ReplayService...
                if (!HasTickedReplayService)
                {
                    // First finish any parallel ticks
                    ((TickableSingletonService)__instance._tickableSingletonService).FinishParallelTick();

                    // Tick it and stop
                    HasTickedReplayService = true;
                    replayService?.DoTick();
                    return true;
                }
                // Otherwise if we're still at the beginning
                // reset the flag
                HasTickedReplayService = false;
            }
            __instance.TickNextBucket();
            NextBucket = __instance._nextBucketIndex;
            return false;
        }

        /// <summary>
        /// An interruption ends a frame's ticking early (a deletion, EntityDeletionEndsFramePatcher; a loaded entity whose
        /// ID was taken). The game's ticker had already taken the frame's buckets off its clock, so the ones left unticked
        /// were lost and the game ran slower. They go back to the ticker's clock instead, to be ticked from the next frame.
        /// At most one tick's worth waits there, so a game interrupted in every frame cannot save up a burst.
        /// </summary>
        private void GiveBackBuckets(TickableBucketService buckets, int unticked)
        {
            if (unticked <= 0 || ticker == null) return;
            float perBucket = ticker._secondsPerBucket;
            if (!(perBucket > 0)) return;
            float oneTick = perBucket * buckets.TotalNumberOfBuckets;
            ticker._accumulatedDeltaTime = Math.Min(oneTick, ticker._accumulatedDeltaTime + unticked * perBucket);
        }

        public bool TickBuckets(TickableBucketService __instance, int numberOfBucketsToTick)
        {
            bucketService = __instance;

            // TODO: I think if number of buckets starts at 0, we should unmark
            // complete full tick and return because it means we're paused...
            // Alternatively, I think that we could use the ReplayService version
            // that only stops ticking if not paused

#if ONE_TICK_PER_UPDATE
            // Forces 1 tick per update
            if (numberOfBucketsToTick != 0)
            {
                // Don't need to add one anymore; singletons are included
                numberOfBucketsToTick = __instance.NumberOfBuckets;
            }
#endif

            while (ShouldTick(__instance, numberOfBucketsToTick--))
            {
                bool tickedReplayService = TickReplayServiceOrNextBucket(__instance);
                // Steam only moves data when this thread asks it to, and the simulation is spread over the frames
                // it needs: at a high speed nearly a whole frame is spent here, so without this every message, in
                // both directions, waited for the end of the frame and the ping grew with the game speed. Right
                // after the replay service ticked, the tick's events are queued for the guests, so send them now.
                Steam.SteamNet.PumpBetweenTicks(force: tickedReplayService);
                if (tickedReplayService)
                {
                    // Refund a bucket if we ticked the ReplayService
                    numberOfBucketsToTick++;
                }
            }

            // Stopped by an interruption: the buckets this frame had left go back to the game's ticker (the loop above
            // counted one past the last it was given).
            if (ShouldInterruptTicking && !ReplayService.HasReplayFailure) GiveBackBuckets(__instance, numberOfBucketsToTick + 1);

            // Tell the TickRequester we've finished this partial (or possibly complete) tick
            OnTickingCompleted();

            // Replace the default behavior entirely
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
        4/19/2025
        // Also check TickNextBucket - we rework everything
		while (numberOfBucketsToTick-- > 0)
		{
			TickNextBucket();
		}
     */
    [HarmonyPatch(typeof(TickableBucketService), nameof(TickableBucketService.TickBuckets))]
    static class TickableBucketServiceTickUpdatePatcher
    {
        static bool Prefix(TickableBucketService __instance, int numberOfBucketsToTick)
        {
            if (ReplayService.HasReplayFailure) return false;
            if (EventIO.IsNull) return true;
            TickingService ts = GetSingleton<TickingService>();
            if (ts == null) return true;
            return ts.TickBuckets(__instance, numberOfBucketsToTick);
        }
    }
}
