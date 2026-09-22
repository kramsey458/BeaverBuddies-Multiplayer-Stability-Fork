using System;
using System.IO;
using BeaverBuddies.IO;
using Timberborn.CharacterMovementSystem;
using Timberborn.EntitySystem;
using Timberborn.WalkingSystem;
using Timberborn.ZiplineMovementSystem;
using UnityEngine;

namespace BeaverBuddies.DesyncDetecter
{
    /// <summary>
    /// Fills a <see cref="WalkerTrace"/> from the game while debug mode is on, and writes it beside the water
    /// diagnostics when a desync is reported. Any failure switches it off for the session; it never throws.
    /// </summary>
    public static class WalkerDiagnostics
    {
        private static readonly WalkerTrace trace = new WalkerTrace();
        private static bool written, failed;

        internal static void Reset()
        {
            trace.Clear();
            written = failed = false;
        }

        // Called for a walking character just before it ticks, so the record is where the previous tick left it.
        internal static void Capture(EntityComponent entity, PathFollower pathFollower, int tick)
        {
            if (!Settings.Debug || tick < 0 || EventIO.IsNull || written || failed) return;
            try
            {
                Vector3 position = pathFollower._transform.position;
                var record = new WalkerRecord
                {
                    EntityId = entity.EntityId.ToString(),
                    Name = entity.Name,
                    X = position.x, Y = position.y, Z = position.z,
                    NextCornerIndex = pathFollower._nextCornerIndex,
                };
                var corners = pathFollower._pathCorners;
                if (corners != null && corners.Count > 0)
                {
                    record.CornerCount = corners.Count;
                    Vector3 last = corners[corners.Count - 1].Position;
                    record.LastCornerX = last.x; record.LastCornerY = last.y; record.LastCornerZ = last.z;
                    int current = pathFollower._nextCornerIndex - 1;
                    if (current >= 0 && current < corners.Count) record.CornerSpeed = corners[current].Speed;
                }
                WalkerSpeedManager speed = entity.GetComponent<WalkerSpeedManager>();
                if (!ReferenceEquals(speed, null))
                {
                    record.BaseSpeed = speed._baseSpeed;
                    record.BonusMultiplier = speed._bonusMultiplier;
                }
                ZiplinePathTracker tracker = entity.GetComponent<ZiplinePathTracker>();
                if (!ReferenceEquals(tracker, null))
                    record.OnZiplineEdge = tracker._fromPoint.HasValue && tracker._toPoint.HasValue;
                ZiplineVisitor visitor = entity.GetComponent<ZiplineVisitor>();
                if (!ReferenceEquals(visitor, null)) record.AnimatedOnZipline = visitor.IsOnZipline;
                trace.Add(tick, record);
            }
            catch (Exception e) { Fail(e); }
        }

        public static void WriteOnDesync()
        {
            if (written || failed || trace.TickCount == 0) return;
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "BeaverBuddiesDiagnostics");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"walkers-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{GuidPatcher.RealNewGuid():N}.tsv");
                using (var writer = new StreamWriter(path)) trace.Write(writer);
                written = true;
                trace.Clear();
                Plugin.Log($"Walker desync diagnostics saved locally: {path}");
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Fail(Exception e)
        {
            failed = true;
            trace.Clear();
            Plugin.LogWarning($"Walker diagnostics unavailable: {e.Message}");
        }
    }
}
