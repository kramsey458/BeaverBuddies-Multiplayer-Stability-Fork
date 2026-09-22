using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.BuildingTools;
using Timberborn.Coordinates;
using Timberborn.DemolishingUI;
using Timberborn.DuplicationSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.PlantingUI;
using Timberborn.ScienceSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.TerrainQueryingSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.WorkSystemUI;
using UnityEngine;

namespace BeaverBuddies.Events
{

    [Serializable]
    class BuildingPlacedEvent : ReplayEvent
    {
        public string prefabName;
        public Vector3Int coordinates;
        public Orientation orientation;
        public bool isFlipped;
        public string duplicationSourceID;

        public override void Replay(IReplayContext context)
        {
            var buildingSpec = GetBuilding(context, prefabName);
            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            var placer = context.GetSingleton<BlockObjectPlacerService>().GetMatchingPlacer(blockObjectSpec);
            Placement placement = new Placement(coordinates, orientation,
                isFlipped ? FlipMode.Flipped : FlipMode.Unflipped);
            // Skip validation for district centers - the preview object interferes with nav mesh
            bool isDistrictCenter = prefabName != null && prefabName.StartsWith("DistrictCenter.");
            if (!isDistrictCenter && !IsPlacementValid(context, placement, buildingSpec))
            {
                Plugin.LogWarning($"Invalid placement for {prefabName} at {coordinates}");
                return;
            }

            BaseComponent duplicationSource = null;
            if (!string.IsNullOrEmpty(duplicationSourceID))
            {
                duplicationSource = GetEntityComponent(context, duplicationSourceID);
            }

            EntitySetup.Builder builder = new EntitySetup.Builder(buildingSpec.Blueprint);
            if ((bool)duplicationSource)
            {
                builder.AddInitComponent(new DuplicationInit(duplicationSource));
            }
            placer.Place(builder, placement);
        }

        // Note: This may not catch every possible invalid placement (e.g. if terrain height changes or something)
        // but I think it should catch the vast majority of cases due to double placement.
        private static bool IsPlacementValid(IReplayContext context, Placement placement, BuildingSpec spec)
        {
            var templateInstantiator = context.GetSingleton<TemplateInstantiator>();
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            // It's a bit wasteful to instantiate the object just to check if it's valid,
            // but this is likely the best choice because:
            // 1) This only happens occasionally, based on UI actions, and
            // 2) There's no easy way to get at the cache of previews the UI uses,
            //    and each blueprint requires a different GameObject, so we can't cache just one.
            // TODO: Check if this is still the case with the new blueprint system.
            GameObject gameObject = templateInstantiator.Instantiate(spec.Blueprint, roots.First().transform);
            gameObject.SetActive(value: false);
            var blockObject = gameObject.GetComponentSlow<BlockObject>();
            blockObject.MarkAsPreviewAndInitialize();
            blockObject.Reposition(placement);
            // Every computer runs this, so a check that reads this computer's hovered previews could split the
            // players: the district-join check passes during a replay (Fixes/DistrictPreviewsReplayFix.cs).
            bool isValid = blockObject.IsValid();
            UnityEngine.Object.Destroy(gameObject);
            return isValid;
        }

        public override string ToActionString()
        {
            return $"Placing {prefabName}, {coordinates}, {orientation}, {isFlipped}";
        }
    }

    [HarmonyPatch(typeof(BuildingPlacer), nameof(BuildingPlacer.Place))]
    class PlacePatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(EntitySetup.Builder entitySetupBuilder, Placement placement)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                string prefabName = ReplayEvent.GetBuildingName(entitySetupBuilder);
                // If there's a duplication source, get the source's EntityID
                var dupInit = (DuplicationInit)entitySetupBuilder._initComponents.Find(c => c is DuplicationInit);
                string duplicationSourceID = dupInit == null ? null : ReplayEvent.GetEntityID(dupInit.DuplicationSource);
                if (!string.IsNullOrEmpty(duplicationSourceID))
                {
                    Plugin.Log($"Found duplication source: {duplicationSourceID} for {prefabName}");
                }

                return new BuildingPlacedEvent()
                {
                    prefabName = prefabName,
                    coordinates = placement.Coordinates,
                    orientation = placement.Orientation,
                    isFlipped = placement.FlipMode.IsFlipped,
                    duplicationSourceID = duplicationSourceID,
                };
            });
        }
    }

    class BuildingsDeconstructedEvent : ReplayEvent
    {
        public List<string> entityIDs = new List<string>();

        public override void Replay(IReplayContext context)
        {
            var entityService = context.GetSingleton<EntityService>();
            foreach (string entityID in entityIDs)
            {
                var entity = GetEntityComponent(context, entityID);
                if (entity == null) continue;
                entityService.Delete(entity);
            }
        }

        public override string ToActionString()
        {
            return $"Deconstructing: {string.Join(", ", entityIDs)}";
        }
    }

    [HarmonyPatch(typeof(BlockObjectDeletionTool<BuildingSpec>), nameof(BlockObjectDeletionTool<BuildingSpec>.DeleteBlockObjects))]
    class BuildingDeconstructionPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(BlockObjectDeletionTool<BuildingSpec> __instance)
        {
            bool result = ReplayEvent.DoPrefix(() =>
            {
                // TODO: If this does work, it may affect other deletions too :(
                List<string> entityIDs = __instance._temporaryBlockObjects
                        .Select(ReplayEvent.GetEntityID)
                        .ToList();

                return new BuildingsDeconstructedEvent()
                {
                    entityIDs = entityIDs,
                };
            });

            if (!result)
            {
                // If we cancel the event, clean up the tool
                __instance._temporaryBlockObjects.Clear();
            }

            return result;
        }
    }

    [Serializable]
    class PlantingAreaMarkedEvent : ReplayEvent
    {
        public List<Vector3Int> inputBlocks;
        public Ray ray;
        public string prefabName;
        // The tiles to mark, levelled by the player who marked them. The game levels the dragged area with its terrain
        // picker, which stops at the layer each player has sliced the view to: played again on another computer, the
        // same ray could give another height and the marks would differ (a desync). Null in events from before.
        public List<Vector3Int> coordinates;

        public const string UNMARK = "Unmark";

        public override void Replay(IReplayContext context)
        {
            var plantingService = context.GetSingleton<PlantingSelectionService>();
            List<Vector3Int> leveled = coordinates
                ?? LevelAbove(inputBlocks, plantingService._terrainAreaService._terrainService.OnGround);
            PlantingLeveledCoordinatesPatcher.Recorded = leveled;
            try
            {
                if (prefabName == UNMARK)
                {
                    plantingService.UnmarkArea(inputBlocks, ray);
                }
                else
                {
                    plantingService.MarkArea(inputBlocks, ray, prefabName);
                }
            }
            finally
            {
                PlantingLeveledCoordinatesPatcher.Recorded = null;
            }
        }

        /// <summary>
        /// An event from before <see cref="coordinates"/>, levelled without the view. The planting tool drags its
        /// rectangle on the level of the terrain it picked first, and the game marks one level above that, where the
        /// ground is: so the marker's own levelling gave the tile above each dragged block that stands on ground.
        /// </summary>
        internal static List<Vector3Int> LevelAbove(IEnumerable<Vector3Int> inputBlocks, Func<Vector3Int, bool> onGround) =>
            (inputBlocks ?? Enumerable.Empty<Vector3Int>())
                .Select(block => new Vector3Int(block.x, block.y, block.z + 1))
                .Where(onGround)
                .ToList();

        public override string ToActionString()
        {
            return $"Planting {(coordinates ?? inputBlocks)?.Count ?? 0} of {prefabName}";
        }

        internal static PlantingAreaMarkedEvent Record(PlantingSelectionService service, IEnumerable<Vector3Int> inputBlocks,
            Ray ray, string prefabName)
        {
            var blocks = new List<Vector3Int>(inputBlocks);
            return new PlantingAreaMarkedEvent()
            {
                prefabName = prefabName,
                ray = ray,
                // The game only levels the dragged blocks, and the replay marks the levelled tiles below instead
                // (PlantingLeveledCoordinatesPatcher), so the blocks themselves are not sent: an area is hundreds of
                // tiles, and this halves what every computer serializes, sends and reads for it.
                inputBlocks = new List<Vector3Int>(),
                // Levelled here, with this player's view: the tiles they saw highlighted.
                coordinates = service._terrainAreaService.InMapLeveledCoordinates(blocks, ray).ToList(),
            };
        }
    }

    [HarmonyPatch(typeof(PlantingSelectionService), nameof(PlantingSelectionService.MarkArea))]
    class PlantingAreaMarkedPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(PlantingSelectionService __instance, IEnumerable<Vector3Int> inputBlocks, Ray ray, string templateName)
        {
            return ReplayEvent.DoPrefix(() => PlantingAreaMarkedEvent.Record(__instance, inputBlocks, ray, templateName));
        }
    }

    [HarmonyPatch(typeof(PlantingSelectionService), nameof(PlantingSelectionService.UnmarkArea))]
    class PlantingAreaUnmarkedPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(PlantingSelectionService __instance, IEnumerable<Vector3Int> inputBlocks, Ray ray)
        {
            return ReplayEvent.DoPrefix(() =>
                PlantingAreaMarkedEvent.Record(__instance, inputBlocks, ray, PlantingAreaMarkedEvent.UNMARK));
        }
    }

    // While a planting event is played, the game's own MarkArea / UnmarkArea act on the tiles the event carries instead
    // of levelling the area again with this computer's view (see PlantingAreaMarkedEvent.coordinates). Everything else
    // they do (which tiles may be planted, the event the game posts) runs as in the game. Outside a replay it does
    // nothing, so the tools' highlighting and every other caller level as before. Priority.Last, the rule for a prefix
    // that replaces the original: another mod's prefix on the levelling runs first. If that prefix skips the original
    // itself, Harmony skips this one too and the replay uses that mod's tiles instead, so a mod that replaces this
    // levelling needs a lockstep review (none is known to).
    [HarmonyPatch(typeof(TerrainAreaService), nameof(TerrainAreaService.InMapLeveledCoordinates))]
    static class PlantingLeveledCoordinatesPatcher
    {
        internal static List<Vector3Int> Recorded;

        [HarmonyPriority(Priority.Last)]
        static bool Prefix(ref IEnumerable<Vector3Int> __result)
        {
            if (Recorded == null) return true;
            __result = Recorded.ToList();
            return false;
        }
    }

    [Serializable]
    class ClearResourcesMarkedEvent : ReplayEvent
    {
        public List<Guid> blocks;
        public Vector3Int start;
        public Vector3Int end;
        public bool markForDemolition;

        public override void Replay(IReplayContext context)
        {
            // The selection was made against the sender's state a few ticks ago,
            // so some entities may already be gone here (e.g. builders finished
            // demolishing an object the same area selection also covered).
            // Skip those instead of failing the whole session; both sides
            // replay at the same tick, so they skip the same entities.
            var blockObjects = ResolveBlockObjects(context, blocks);
            if (blocks.Count > 0 && blockObjects.Count == 0)
            {
                Plugin.LogWarning($"Skipping {ToActionString()}: none of the {blocks.Count} entities exist anymore");
                return;
            }
            if (markForDemolition)
            {
                context.GetSingleton<DemolishableSelectionTool>().ActionCallback(blockObjects, start, end, false, false);
            }
            else
            {
                context.GetSingleton<DemolishableUnselectionTool>().ActionCallback(blockObjects, start, end, false, false);
            }
        }

        internal static List<BlockObject> ResolveBlockObjects(IReplayContext context, IEnumerable<Guid> ids)
        {
            var result = new List<BlockObject>();
            foreach (Guid id in ids)
            {
                // Logs a warning if the entity or component is missing.
                var blockObject = GetComponent<BlockObject>(context, id.ToString());
                if (blockObject == null) continue;
                result.Add(blockObject);
            }
            return result;
        }

        public override string ToActionString()
        {
            return $"Setting {blocks.Count()} as marked: {markForDemolition}";
        }

        public static bool DoPrefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool forDemolition)
        {
            return DoPrefix(() =>
            {
                var ids = blockObjects.Select(obj => obj.GetComponent<EntityComponent>().EntityId);
                return new ClearResourcesMarkedEvent()
                {
                    blocks = ids.ToList(),
                    start = start,
                    end = end,
                    markForDemolition = forDemolition
                };
            });
        }
    }

    [HarmonyPatch(typeof(DemolishableSelectionTool), nameof(DemolishableSelectionTool.ActionCallback))]
    class DemolishableSelectionServiceMarkPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)
        {
            return ClearResourcesMarkedEvent.DoPrefix(blockObjects, start, end, true);
        }
    }

    [HarmonyPatch(typeof(DemolishableUnselectionTool), nameof(DemolishableUnselectionTool.ActionCallback))]
    class DemolishableSelectionServiceUnmarkPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)
        {
            return ClearResourcesMarkedEvent.DoPrefix(blockObjects, start, end, false);
        }
    }

    [Serializable]
    class TreeCuttingAreaEvent : ReplayEvent
    {
        public List<Vector3Int> coordinates;
        public bool wasAdded;

        public override void Replay(IReplayContext context)
        {
            var treeService = context.GetSingleton<TreeCuttingArea>();
            if (wasAdded)
            {
                treeService.AddCoordinates(coordinates);
            }
            else
            {
                treeService.RemoveCoordinates(coordinates);
            }
        }

        public override string ToActionString()
        {
            string verb = wasAdded ? "Added" : "Removed";
            return $"{verb} tree planting coordinate {coordinates.Count()}";
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.AddCoordinates))]
    class TreeCuttingAreaAddedPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(IEnumerable<Vector3Int> coordinates)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new TreeCuttingAreaEvent()
                {
                    coordinates = new List<Vector3Int>(coordinates),
                    wasAdded = true,
                };
            });
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.RemoveCoordinates))]
    class TreeCuttingAreaRemovedPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(IEnumerable<Vector3Int> coordinates)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new TreeCuttingAreaEvent()
                {
                    coordinates = new List<Vector3Int>(coordinates),
                    wasAdded = false,
                };
            });
        }
    }

    [Serializable]
    class BuildingUnlockedEvent : ReplayEvent
    {
        public string buildingName;

        public override void Replay(IReplayContext context)
        {
            var building = GetBuilding(context, buildingName);
            if (building == null) return;
            context.GetSingleton<BuildingUnlockingService>().Unlock(building);

            var toolButtonService = context.GetSingleton<ToolButtonService>();
            var toolUnlockingService = toolButtonService._toolUnlockingService;

            foreach (ToolButton toolButton in toolButtonService.ToolButtons)
            {
                var tool = toolButton.Tool;
                BlockObjectTool blockObjectTool = tool as BlockObjectTool;
                if (blockObjectTool == null)
                {
                    continue;
                }
                BuildingSpec toolBuilding = blockObjectTool.Template.GetSpec<BuildingSpec>();
                if (toolBuilding == building)
                {
                    Plugin.Log("Unlocking tool for building: " + buildingName);
                    context.GetSingleton<UnlockedPlantableGroupsRegistry>().AddUnlockedPlantableGroups(toolBuilding);
                    // Call Unlock to remove from _activeLockers and post ToolUnlockedEvent
                    if (toolUnlockingService != null && toolUnlockingService.IsLocked(tool))
                    {
                        toolUnlockingService.Unlock(tool);
                    }
                }
            }
        }

        public override string ToActionString()
        {
            return $"Unlocking building: {buildingName}";
        }
    }

    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.Unlock))]
    class BuildingUnlockingServiceUnlockPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(BuildingSpec buildingSpec)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new BuildingUnlockedEvent()
                {
                    buildingName = buildingSpec.Blueprint.Name,
                };
            });
        }
    }

    [Serializable]
    class WorkingHoursChangedEvent : ReplayEvent
    {
        public int hours;

        public override void Replay(IReplayContext context)
        {
            var panel = context.GetSingleton<WorkingHoursPanel>();
            panel._hours = hours;
            panel.OnHoursChanged();
        }

        public override string ToActionString()
        {
            return $"Setting working hours: {hours}";
        }
    }

    [HarmonyPatch(typeof(WorkingHoursPanel), nameof(WorkingHoursPanel.OnHoursChanged))]
    class WorkingHoursPanelOnHoursChangedPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(WorkingHoursPanel __instance)
        {
            bool value = ReplayEvent.DoPrefix(() =>
            {
                return new WorkingHoursChangedEvent()
                {
                    hours = __instance._hours,
                };
            });

            // Update the title if we're actually calling this event
            if (!value) __instance.UpdateTitle();
            return value;
        }
    }

    [Serializable]
    class DuplicationEvent : ReplayEvent
    {
        public string sourceEntityID;
        public string targetEntityID;

        public override void Replay(IReplayContext context)
        {
            var duplicator = new Duplicator();
            duplicator.Duplicate(
                GetEntityComponent(context, sourceEntityID),
                GetEntityComponent(context, targetEntityID)
            );
        }

        public override string ToActionString()
        {
            return $"Duplicating properties from {sourceEntityID} to {targetEntityID}";
        }
    }

    [ManualMethodOverwrite]
    /*
     * 11/26/2025
     * This isn't a real manual method overwrite, but it still needs
     * to be reviewed when the Timberborn code updates. It makes a strong
     * assumption that Duplicator.Duplicate is only called by UI events
     * and that it's the only callback that gets called when a building
     * is placed (see BuildingPlacedEvent).
     * Check
     * * IBlockObjectPlacer.Place: Make sure it's only called with
     *   a callback that calls this method.
     * * Duplicator.Duplicate: Make sure it's only called by UI actions.
     * * DuplicateSettingsTool: Make sure this continues not to do anything
     *   other than other than registering the change so it can be undone and
     *   that undos are still only supported in the map editor.
     */
    [HarmonyPatch(typeof(Duplicator), nameof(Duplicator.Duplicate))]
    class DuplicatorDuplicatePatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(BaseComponent sourceEntity, BaseComponent targetEntity)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new DuplicationEvent()
                {
                    sourceEntityID = ReplayEvent.GetEntityID(sourceEntity),
                    targetEntityID = ReplayEvent.GetEntityID(targetEntity),
                };
            });
        }
    }
}