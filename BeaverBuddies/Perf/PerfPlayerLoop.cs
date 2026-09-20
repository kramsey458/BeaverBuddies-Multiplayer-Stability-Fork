using System;
using System.Collections.Generic;
using TimberNet.Perf;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// Times each of Unity's phases of a frame (waiting for the previous frame to be presented, input, scripts, late scripts, drawing),
    /// by putting a marker at the start and at the end of each phase in the player loop. That says where a frame's time that is outside
    /// everything this mod measures goes: in the game's scripts, or in drawing and waiting for the graphics card or vertical sync. The
    /// markers do nothing but read the clock, are removed when the log ends, and are never installed when the log is off.
    /// </summary>
    internal static class PerfPlayerLoop
    {
        // The two edges of each phase are told apart by type, because Unity identifies a system by its type.
        sealed class StartEdge { }
        sealed class EndEdge { }
        sealed class Marker<TPhase, TEdge> { }

        // The order is the order of the phase columns: plTime, plInit, plEarly, plFixed, plPre, plUpdate, plLate, plPost.
        static readonly Type[] phaseTypes =
        {
            typeof(TimeUpdate), typeof(Initialization), typeof(EarlyUpdate), typeof(FixedUpdate),
            typeof(PreUpdate), typeof(UnityEngine.PlayerLoop.Update), typeof(PreLateUpdate), typeof(PostLateUpdate),
        };

        internal static int Installed { get; private set; }

        internal static bool Install()
        {
            Installed = 0;
            try
            {
                Uninstall();
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem[] top = loop.subSystemList;
                if (top == null) return false;
                for (int i = 0; i < top.Length; i++)
                {
                    int phase = Array.IndexOf(phaseTypes, top[i].type);
                    if (phase < 0) continue;
                    PlayerLoopSystem system = top[i];
                    PlayerLoopSystem[] inner = system.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                    var list = new List<PlayerLoopSystem>(inner.Length + 2) { Edge(phase, true) };
                    list.AddRange(inner);
                    list.Add(Edge(phase, false));
                    system.subSystemList = list.ToArray();
                    top[i] = system;
                    Installed++;
                }
                if (Installed == 0) return false;
                loop.subSystemList = top;
                PlayerLoop.SetPlayerLoop(loop);
                return true;
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Could not time Unity's frame phases: " + error.Message);
                Installed = 0;
                return false;
            }
        }

        internal static void Uninstall()
        {
            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem[] top = loop.subSystemList;
                if (top == null) return;
                bool changed = false;
                for (int i = 0; i < top.Length; i++)
                {
                    PlayerLoopSystem[] inner = top[i].subSystemList;
                    if (inner == null) continue;
                    var kept = new List<PlayerLoopSystem>(inner.Length);
                    foreach (PlayerLoopSystem system in inner)
                    {
                        if (system.type != null && system.type.IsGenericType && system.type.GetGenericTypeDefinition() == typeof(Marker<,>)) changed = true;
                        else kept.Add(system);
                    }
                    if (kept.Count == inner.Length) continue;
                    PlayerLoopSystem copy = top[i];
                    copy.subSystemList = kept.ToArray();
                    top[i] = copy;
                }
                if (!changed) return;
                loop.subSystemList = top;
                PlayerLoop.SetPlayerLoop(loop);
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Could not remove the frame phase timing: " + error.Message);
            }
            finally { Installed = 0; }
        }

        static PlayerLoopSystem Edge(int phase, bool start)
        {
            Type marker = typeof(Marker<,>).MakeGenericType(phaseTypes[phase], start ? typeof(StartEdge) : typeof(EndEdge));
            return new PlayerLoopSystem
            {
                type = marker,
                updateDelegate = () => PerfProbe.PhaseMark(phase, start),
            };
        }
    }
}
