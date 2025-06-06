﻿using BepInEx.Unity.IL2CPP.Utils.Collections;
using KindredSchematics.Data;
using KindredSchematics.JsonConverters;
using KindredSchematics.Patches;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Physics;
using ProjectM.Roofs;
using ProjectM.Shared;
using ProjectM.Tiles;
using Stunlock.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.UniversalDelegates;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace KindredSchematics.Services
{
    internal class SchematicService
    {
        static readonly string CONFIG_PATH = Path.Combine(BepInEx.Paths.ConfigPath, MyPluginInfo.PLUGIN_NAME);

        struct Schematic
        {
            public string version { get; set; }
            public Vector3? location { get; set; }
            public int? territoryIndex { get; set; }
            public Aabb boundingBox { get; set; }
            public Aabb[] aabbs { get; set; }
            public EntityData[] entities { get; set; }
        }

        struct HeartInfo
        {
            public Entity CastleHeart;
            public bool OwnerDoors;
            public bool OwnerChests;
        };

        Dictionary<Entity, HeartInfo> fallbackHeart = [];
        Entity neutralTeam;
        public Entity NeutralTeam => neutralTeam;

        readonly List<Entity> usersClearingEntireArea = [];
        readonly List<Entity> usersPlacingOffGrid = [];

        GameObject schematicSvcGameObject;
        IgnorePhysicsDebugSystem schematicMonoBehaviour;

        HashSet<PrefabGUID> prefabsAllowedToDestroy = [];


        public SchematicService()
        {
            schematicSvcGameObject = new GameObject("SchematicService");
            schematicMonoBehaviour = schematicSvcGameObject.AddComponent<IgnorePhysicsDebugSystem>();

            foreach(var (prefabName, prefabGUID) in Core.PrefabCollection.SpawnableNameToPrefabGuidDictionary)
            {
                if(prefabName.StartsWith("TM_") || prefabName.StartsWith("Chain_") || 
                   (Core.PrefabCollection._PrefabGuidToEntityMap.TryGetValue(prefabGUID, out var prefab) &&
                    prefab.Has<CastleBuildingFusedRoot>()))
                {
                    prefabsAllowedToDestroy.Add(prefabGUID);
                }
            }
            var neutralTeams = Helper.GetEntitiesByComponentType<NeutralTeam>(includeDisabled: true);
            neutralTeam = neutralTeams.ToArray().FirstOrDefault();
            neutralTeams.Dispose();
        }

        public void StartCoroutine(IEnumerator routine)
        {
            schematicMonoBehaviour.StartCoroutine(routine.WrapToIl2Cpp());
        }

        public IEnumerable<string> GetSchematics()
        {
            if (!Directory.Exists(CONFIG_PATH))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(CONFIG_PATH, "*.schematic"))
            {
                yield return Path.GetFileNameWithoutExtension(file);
            }
        }

        public static JsonSerializerOptions GetJsonOptions()
        {
            var options = new JsonSerializerOptions()
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
            };
            options.Converters.Add(new AabbConverter());
            options.Converters.Add(new AssetGUIDConverter());
            options.Converters.Add(new CurveReferenceConverter());
            options.Converters.Add(new int2Converter());
            options.Converters.Add(new PrefabGUIDConverter());
            options.Converters.Add(new QuaternionConverter());
            options.Converters.Add(new Vector2Converter());
            options.Converters.Add(new Vector3Converter());
            return options;
        }

        public void SaveSchematic(string name, float3? location = null, float? radius = null, Vector2? halfSize = null, int? territoryIndex = null)
        {
            var startTime = Time.realtimeSinceStartup;
            float GetElapseTime() => Time.realtimeSinceStartup - startTime;
            Core.Log.LogInfo($"{GetElapseTime()} Starting to save {name}");
            var schematic = new Schematic
            {
                version = "1.0.1",
                entities = []
            };

            if (territoryIndex.HasValue)
                schematic.territoryIndex = territoryIndex;
            else
            {
                var gridLocation = Helper.ConvertPosToTileGrid(location.Value);
                schematic.boundingBox = new Aabb { Min = gridLocation, Max = gridLocation };
                schematic.location = location;
            }

            IEnumerable<Entity> entities;
            if (radius != null) entities = Helper.GetAllEntitiesInRadius<Translation>(location.Value.xz, radius.Value);
            else if (halfSize != null) entities = Helper.GetAllEntitiesInBox<Translation>(location.Value.xz, halfSize.Value);
            else if (territoryIndex != null) entities = Helper.GetAllEntitiesInTerritory<Translation>(territoryIndex.Value);
            else
            {
                Core.Log.LogError($"Schematic {name} has no radius, halfSize, or territory index");
                return;
            }

            Core.Log.LogInfo($"{GetElapseTime()} Gathered entities for {name}");

            var entityPrefabDiffs = new List<EntityData>();
            var aabbs = new List<Aabb>();
            var entitiesSaving = entities.Where(entity =>
            {
                if (entity.Has<CastleHeart>())
                    return false;

                if (!entity.Has<PrefabGUID>())
                    return false;

                if (entity.Has<RoofTileData>())
                    return false;

                if (entity.Has<ChunkWaypoint>() && !entity.Has<BlueprintData>())
                    return false;

                if (entity.Has<ChunkPortal>())
                    return false;

                var prefabGUID = entity.Read<PrefabGUID>();

                // For some reason this prefab is missing the correct stuff on the server
                if (prefabGUID == Prefabs.TM_Castle_Wall_Tier02_Stone_EntranceCrown)
                    return false;

                var prefabName = prefabGUID.LookupName();
                return prefabName.StartsWith("TM_") || prefabName.StartsWith("Chain_") || prefabName.StartsWith("BP_");
            });

            var entityMapper = new EntityMapper(entitiesSaving);
            Core.Log.LogInfo($"{GetElapseTime()} Gathered and Filtered entities for {name} and now saving");
            for (var i = 1; i < entityMapper.Count; ++i)
            {
                var entity = entityMapper[i];
                var data = EntityPrefabDiff.DiffFromPrefab(entity, entityMapper);
                entityPrefabDiffs.Add(data);
                if (territoryIndex == null && Helper.GetAabb(entity, out var aabb))
                {

                    aabbs.Add(aabb);
                    aabb.Include(schematic.boundingBox);
                    schematic.boundingBox = aabb;
                }

                if (entity.Has<CastleFloor>())
                {
                    var castleFloor = entity.Read<CastleFloor>();
                    if (castleFloor.NeighbourFloorNorth.Entity != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.NeighbourFloorNorth.Entity);
                    }
                    if (castleFloor.NeighbourFloorEast.Entity != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.NeighbourFloorEast.Entity);
                    }
                    if (castleFloor.NeighbourFloorSouth.Entity != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.NeighbourFloorSouth.Entity);
                    }
                    if (castleFloor.NeighbourFloorWest.Entity != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.NeighbourFloorWest.Entity);
                    }
                    if (castleFloor.NeighbourFloorUp.Entity != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.NeighbourFloorUp.Entity);
                    }
                    if (castleFloor.NeighbourFloorDown.Entity != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.NeighbourFloorDown.Entity);
                    }
                    if (castleFloor.WallNorth != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.WallNorth);
                    }
                    if (castleFloor.WallEast != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.WallEast);
                    }
                    if (castleFloor.WallSouth != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.WallSouth);
                    }
                    if (castleFloor.WallWest != Entity.Null)
                    {
                        var neighbour = entityMapper.IndexOf(castleFloor.WallWest);
                    }
                }
            }

            Core.Log.LogInfo($"{GetElapseTime()} Saved and now Starting to merge aabbs");
            var startNumAabbs = aabbs.Count;
            AabbHelper.MergeAabbsTogether(aabbs);
            Core.Log.LogInfo($"{GetElapseTime()} Reduced by {startNumAabbs - aabbs.Count} aabbs from {startNumAabbs} to {aabbs.Count}");
            if (territoryIndex == null)
            {
                schematic.aabbs = aabbs.ToArray();

                // Time to do a second pass based off the AABBs to see if there was entities we missed saving out
                Core.Log.LogInfo($"{GetElapseTime()} Starting to check for entities we missed saving out");
                var startEntityMapperIndex = entityMapper.Count;
                var entitiesToCheck = Helper.GetAllEntitiesInTileAabb<Translation>(schematic.boundingBox).
                        Where(x =>
                        {
                            if (entityMapper.Contains(x))
                                return false;

                            foreach (var aabb in aabbs)
                            {
                                if (Helper.IsEntityInAabb(x, aabb))
                                {
                                    entityMapper.AddEntity(x);
                                    return true;
                                }
                            }
                            return false;
                        });

                for (var i = startEntityMapperIndex; i < entityMapper.Count; ++i)
                {
                    var data = EntityPrefabDiff.DiffFromPrefab(entityMapper[i], entityMapper);
                    entityPrefabDiffs.Add(data);
                }

                Core.Log.LogInfo($"{GetElapseTime()} Finished checking for entities we missed saving out and added {entityMapper.Count - startEntityMapperIndex} more");
            }

            schematic.entities = entityPrefabDiffs.ToArray();

            Core.Log.LogInfo($"{GetElapseTime()} Serializing {schematic.entities.Length} entities for {name}");
            var json = JsonSerializer.Serialize(schematic, GetJsonOptions());

            Core.Log.LogInfo($"{GetElapseTime()} Writing {name}.schematic");
            if (!Directory.Exists(CONFIG_PATH))
            {
                Directory.CreateDirectory(CONFIG_PATH);
            }
            File.WriteAllText($"{CONFIG_PATH}/{name}.schematic", json);
            Core.Log.LogInfo($"{GetElapseTime()} Finished writing {name}.schematic");
        }

        public Entity CurUserEntity { get; private set; }
        public Entity CurCharEntity { get; private set; }
        public string LoadSchematic(string name, Entity userEntity, Entity charEntity, float expandClear, Vector3? newCenter = null)
        {
            CurUserEntity = userEntity;
            CurCharEntity = charEntity;

            string json;
            try
            {
                json = File.ReadAllText($"{CONFIG_PATH}/{name}.schematic");
            }
            catch (FileNotFoundException)
            {
                return $"Schematic not found";
            }

            Schematic schematic;
            try
            {
                schematic = JsonSerializer.Deserialize<Schematic>(json, GetJsonOptions());
            }
            catch (JsonException e)
            {
                Core.Log.LogError($"Error loading schematic {name}: {e.Message}");
                return "Error in file";
            }

            if (schematic.version != "1.0.1")
            {
                return $"Has an unsupported version '{schematic.version}' loading old versions is coming soon";
            }

            Core.Log.LogInfo($"Loading Schematic {name}");

            Core.StartCoroutine(FinishLoadingSchematic(userEntity, charEntity, expandClear, newCenter, schematic));

            return null;
        }

        private IEnumerator FinishLoadingSchematic(Entity userEntity, Entity charEntity, float expandClear, Vector3? newCenter, Schematic schematic)
        {
            const float MESSAGE_FREQUENCY = 2.5f;
            var startTime = Time.realtimeSinceStartup;
            float GetElapseTime() => Time.realtimeSinceStartup - startTime;
            var lastYieldTime = Time.realtimeSinceStartup;
            var timeSinceLastMessage = Time.realtimeSinceStartup;

            bool MessageUser(string message, bool always = false)
            {
                if (always || Time.realtimeSinceStartup - timeSinceLastMessage > MESSAGE_FREQUENCY)
                {
                    var fixedMsg = new FixedString512Bytes(message);
                    ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, userEntity.Read<User>(), ref fixedMsg);
                    timeSinceLastMessage = Time.realtimeSinceStartup;
                    return true;
                }
                return false;
            }

            var translation = Vector3.zero;
            var heartAabbsInLoadArea = new List<Aabb>();
            List<Entity> entitiesToDestroy = [];
            if (schematic.location.HasValue)
            {
                var center = newCenter ?? schematic.location.Value;
                translation = newCenter != null ? center - schematic.location.Value : Vector3.zero;

                // Figure out the translation to keep it on the grid
                if (!usersPlacingOffGrid.Contains(userEntity))
                {
                    translation.x = Mathf.Round(translation.x / 5) * 5;
                    translation.y = Mathf.Round(translation.y);
                    translation.z = Mathf.Round(translation.z / 5) * 5;
                }

                var gridTranslation = new float3(translation.x * 2, translation.y, translation.z * 2);

                var aabb = schematic.boundingBox;
                aabb.Min += gridTranslation;
                aabb.Max += gridTranslation;
                aabb.Expand(expandClear);

                Core.Log.LogInfo($"{GetElapseTime():f4} Getting entities in {aabb}");
                var entitiesToCheck = Helper.GetAllEntitiesInTileAabb<Translation>(aabb).
                    Where(x =>
                    {
                        if (x.Has<CastleHeart>())
                        {
                            var pos = x.Read<Translation>().Value;
                            var heartAabb = new Aabb
                            {
                                Min = new float3(pos.x - 2.5f, pos.y, pos.z - 2.5f),
                                Max = new float3(pos.x + 2.5f, pos.y + 0.1f, pos.z + 2.5f)
                            };
                            heartAabbsInLoadArea.Add(heartAabb);
                            return false;
                        }

                        return true;
                    }).ToArray();

                Core.Log.LogInfo($"{GetElapseTime():f4} Checking {entitiesToCheck.Length} entities to see if they need to be cleared");
                MessageUser($"Checking {entitiesToCheck.Length} entities to see if they need to be cleared");
                var aabbArray = new Aabb[schematic.aabbs.Length];
                for (var i = 0; i < schematic.aabbs.Length; i++)
                {
                    var newAabb = schematic.aabbs[i];
                    newAabb.Min += gridTranslation;
                    newAabb.Max += gridTranslation;
                    newAabb.Expand(expandClear);
                    aabbArray[i] = newAabb;
                }

                for (var i = 0; i < entitiesToCheck.Length; i++)
                {
                    var entity = entitiesToCheck[i];
                    if (Time.realtimeSinceStartup - lastYieldTime > 0.05f)
                    {
                        if (MessageUser($"Checked {i}/{entitiesToCheck.Length} for deletion so far with {entitiesToDestroy.Count} marked to be deleted"))
                            Core.Log.LogInfo($"{GetElapseTime():f4} Checked {i}/{entitiesToCheck.Length} for deletion so far with {entitiesToDestroy.Count} marked to be deleted");
                        lastYieldTime = Time.realtimeSinceStartup;
                        yield return null;
                    }

                    if (!entity.Has<PrefabGUID>())
                        continue;

                    if (!prefabsAllowedToDestroy.Contains(entity.Read<PrefabGUID>()))
                        continue;

                    // Keep entities protected by the heart
                    foreach (var heartAabb in heartAabbsInLoadArea)
                    {
                        if (Helper.IsEntityInAabb(entity, heartAabb))
                            continue;
                    }

                    foreach (var aabbToTest in aabbArray)
                    {
                        if (Helper.IsEntityInAabb(entity, aabbToTest))
                        {
                            entitiesToDestroy.Add(entity);
                            break;
                        }
                    }
                }
            }
            else if (schematic.territoryIndex.HasValue)
            {
                entitiesToDestroy.AddRange(Helper.GetAllEntitiesInTerritory<Translation>(schematic.territoryIndex.Value).
                    Where(x =>
                    {
                        if (!x.Has<PrefabGUID>())
                            return false;

                        if (x.Has<CastleHeart>())
                        {
                            var pos = x.Read<Translation>().Value;
                            var heartAabb = new Aabb
                            {
                                Min = new float3(pos.x - 2.5f, pos.y, pos.z - 2.5f),
                                Max = new float3(pos.x + 2.5f, pos.y + 0.1f, pos.z + 2.5f)
                            };
                            heartAabbsInLoadArea.Add(heartAabb);
                            return false;
                        }

                        var prefabName = x.Read<PrefabGUID>().LookupName();
                        return prefabName.StartsWith("TM_") || prefabName.StartsWith("Chain_") || prefabName.StartsWith("BP_") || x.Has<CastleBuildingFusedRoot>();
                    }));
            }

            Core.Log.LogInfo($"{GetElapseTime():f4} Filter {entitiesToDestroy.Count()} entities for clearing");
            MessageUser($"Now deleting {entitiesToDestroy.Count()} entities before loading in the schematic", true);
            Core.RespawnPrevention.PreventRespawns();
            yield return null;
            var entitiesDestroyingThisFrame = 0;
            foreach (var entity in entitiesToDestroy)
            {
                Helper.DestroyEntityAndCastleAttachments(entity);
                entitiesDestroyingThisFrame++;

                if (entitiesDestroyingThisFrame > 50)
                {
                    entitiesDestroyingThisFrame = 0;
                    yield return null;
                    lastYieldTime = Time.realtimeSinceStartup;
                }
            }

            MessageUser("Starting to load in the schematic", true);
            yield return null;

            var teamValue = charEntity.Read<Team>().Value;
            GetFallbackCastleHeart(charEntity, out var castleHeartEntity, out var ownerDoors, out var ownerChests);

            var defaultHeartInfo = new HeartInfo
            {
                CastleHeart = castleHeartEntity,
                OwnerDoors = ownerDoors,
                OwnerChests = ownerChests
            };

            var territoryToHeartInfo = new Dictionary<int, HeartInfo>
            {
                { -1, defaultHeartInfo }
            };

            // Disable spawn chain system for one frame
            InitializeNewSpawnChainSystem_Patch.skipOnce = true;

            // First pass create all the entitiesToDestroy
            var createdEntities = new Entity[schematic.entities.Length + 1];
            createdEntities[0] = Entity.Null;

            Core.Log.LogInfo($"{GetElapseTime():f4} Figuring out dependency groups");
            var dependentGroups = new Dictionary<int, List<int>>();
            // Initialize dependent groups with a list of everyone
            for (var i = 0; i < schematic.entities.Length; ++i)
            {
                dependentGroups.Add(i, new List<int> { i });
            }

            var dependencies = schematic.entities
                                        .Select((x, index) => ComponentSaver.ComponentSaver.GetDependencies(x.componentData)
                                            .Where(depIndex => depIndex != index+1)
                                            .ToArray())
                                        .ToArray();

            // Grouping entities that are dependent on each other into groups that load together
            for (var i = 0; i < schematic.entities.Length; ++i)
            {
                var group = dependentGroups[i];
                var curDependencies = dependencies[i][..]; // Copy as we are modifying the dependencies
                foreach (var dependentOn in curDependencies)
                {
                    var dependentIndex = dependentOn - 1;

                    // Have they already have been combined
                    if (group.Contains(dependentIndex))
                        continue;

                    if (dependencies[dependentIndex].Contains(i + 1))
                    {
                        var otherGroup = dependentGroups[dependentIndex];
                        foreach (var k in otherGroup)
                        {
                            dependentGroups[k] = group;
                            group.Add(k);
                        }

                        var newDependencies = dependencies[dependentIndex]
                                                .Union(dependencies[i])
                                                .Where(x => !group.Contains(x-1))
                                                .ToArray();

                        foreach (var k in group)
                        {
                            dependencies[k] = newDependencies;
                        }
                    }
                }
            }
            Core.Log.LogInfo($"{GetElapseTime():f4} Finished figuring out {dependentGroups.Count} dependency groups from {schematic.entities.Length} entitiesToDestroy");

            var entitiesLoaded = new HashSet<int>();
            var entitiesLoadedThisFrame = 0;
            do
            {
                var time = Core.ServerTime;

                List<int> entityGroupToLoad = [];
                for (var i = schematic.entities.Length - 1; i >= 0; --i)
                {
                    if (entitiesLoaded.Contains(i))
                        continue;

                    // Check if we have all the groupDependencies
                    if (dependencies[i].Any(x => !entitiesLoaded.Contains(x - 1)))
                        continue;

                    // Lets load the group with these entities
                    entityGroupToLoad = dependentGroups[i];
                    break;
                }

                if (entityGroupToLoad.Count == 0)
                {
                    Core.Log.LogInfo($"{GetElapseTime():f4} Failed to find entity group to load so loading the remaining now");
                    yield return null;
                    for (var i = 0; i < schematic.entities.Length; ++i)
                        if (!entitiesLoaded.Contains(i))
                            entityGroupToLoad.Add(i);
                }

                // Check to see if this group has a TransitionWhenInventoryIsEmpty in it and then skip it but say they are loaded
                var invalidGroup = false;
                foreach (var i in entityGroupToLoad)
                {
                    var entityData = schematic.entities[i];
                    if (!Core.PrefabCollection._PrefabLookupMap.TryGetValue(entityData.prefab, out var prefab))
                    {
                        Core.Log.LogWarning($"Missing prefab {entityData.prefab.GuidHash} so not loading Entity group with {string.Join(", ", entityGroupToLoad.Select(x => x.ToString()))}");
                        invalidGroup = true;
                        break;
                    }

                    if (prefab.Has<TransitionWhenInventoryIsEmpty>())
                    {
                        Core.Log.LogWarning($"Can't spawn in {i} as {entityData.prefab.LookupName()} as it has TransitionWhenInventoryIsEmpty on it so not loading Entity group with {string.Join(", ", entityGroupToLoad.Select(x => x.ToString()))}");
                        invalidGroup = true;
                        break;
                    }

                    if (prefab.Has<ChunkPortal>())
                    {
                        Core.Log.LogWarning($"Can't spawn in {i} as {entityData.prefab.LookupName()} as it has ChunkPortal on it so not loading Entity group with {string.Join(", ", entityGroupToLoad.Select(x => x.ToString()))}");
                        invalidGroup = true;
                        break;
                    }

                    if (prefab.Has<ChunkWaypoint>() && !prefab.Has<BlueprintData>())
                    {
                        Core.Log.LogWarning($"Can't spawn in {i} as {entityData.prefab.LookupName()} as it has ChunkWaypoint on it so not loading Entity group with {string.Join(", ", entityGroupToLoad.Select(x => x.ToString()))}");
                        invalidGroup = true;
                        break;
                    }
                }

                var missingDependents = dependencies[entityGroupToLoad[0]].Where(x => createdEntities[x] == Entity.Null && !entityGroupToLoad.Contains(x));
                if (missingDependents.Any())
                {
                    Core.Log.LogWarning($"Missing dependents for Entity group with {string.Join(", ", entityGroupToLoad.Select(x => x.ToString()))} so unable to spawn because of missing " +
                                        $"{String.Join(",", missingDependents.Select(x => $"{x} ({schematic.entities[x].prefab.LookupName()})"))}");
                    invalidGroup = true;
                }

                foreach (var i in entityGroupToLoad)
                {
                    entitiesLoaded.Add(i);
                    entitiesLoadedThisFrame++;
                }

                if (invalidGroup) continue;

                foreach (var i in entityGroupToLoad)
                {
                    var entityData = schematic.entities[i];

                    if (entityData.prefab.GuidHash == 0)
                        continue;

                    if (Core.PrefabCollection._PrefabLookupMap.TryGetValue(entityData.prefab, out var prefab))
                    {
                        //Core.Log.LogInfo($"Spawning {i} as {entityData.prefab.LookupName()}");
                        Entity entity = SpawnEntity(userEntity, translation, entityData, prefab);

                        var territoryIndex = Helper.GetEntityTerritoryIndex(entity);

                        var heartInfo = defaultHeartInfo;
                        if (!territoryToHeartInfo.TryGetValue(territoryIndex, out heartInfo))
                        {
                            heartInfo = defaultHeartInfo;
                            var heartEntity = Core.CastleTerritory.GetHeartForTerritory(territoryIndex);
                            if (!heartEntity.Equals(Entity.Null))
                            {
                                heartInfo.CastleHeart = heartEntity;
                            }
                            territoryToHeartInfo.Add(territoryIndex, heartInfo);
                        }

                        if (!heartInfo.OwnerDoors && entity.Has<Door>() ||
                            !heartInfo.OwnerChests && Helper.EntityIsChest(entity))

                        {
                            if (entity.Has<CastleHeartConnection>())
                            {
                                entity.Write(new CastleHeartConnection { CastleHeartEntity = Entity.Null });
                            }

                            var teamRef = neutralTeam;
                            if (entity.Has<Team>())
                            {
                                var teamData = teamRef.Read<TeamData>();
                                entity.Write(new Team() { Value = teamData.TeamValue, FactionIndex = -1 });

                                entity.Add<UserOwner>();
                                entity.Write(heartInfo.CastleHeart.Read<UserOwner>());
                            }

                            if (entity.Has<TeamReference>() && !teamRef.Equals(Entity.Null))
                            {
                                var t = new TeamReference();
                                t.Value._Value = teamRef;
                                entity.Write(t);
                            }
                        }
                        else if (heartInfo.CastleHeart != Entity.Null)
                        {
                            if (entity.Has<CastleHeartConnection>())
                            {
                                entity.Write(new CastleHeartConnection { CastleHeartEntity = heartInfo.CastleHeart });
                            }

                            if (!entityData.notCastleTeam.HasValue || !entityData.notCastleTeam.Value)
                            {
                                var teamRef = (Entity)heartInfo.CastleHeart.Read<TeamReference>().Value;
                                if (entity.Has<Team>())
                                {
                                    var teamData = teamRef.Read<TeamData>();
                                    entity.Write(new Team() { Value = teamData.TeamValue, FactionIndex = -1 });

                                    entity.Add<UserOwner>();
                                    entity.Write(heartInfo.CastleHeart.Read<UserOwner>());
                                }

                                if (entity.Has<TeamReference>() && !teamRef.Equals(Entity.Null))
                                {
                                    var t = new TeamReference();
                                    t.Value._Value = teamRef;
                                    entity.Write(t);
                                }
                            }
                        }
                        else if (entity.Has<EditableTileModel>())
                        {
                            var etm = entity.Read<EditableTileModel>();
                            etm.CanDismantle = false;
                            entity.Write(etm);
                        }

                        if (territoryIndex == -1 && entity.Has<TileModel>())
                        {
                            if (!entity.Has<Immortal>())
                                entity.Add<Immortal>();
                            entity.Write(new Immortal() { IsImmortal = true });
                            if (entity.Has<EntityCategory>())
                            {
                                var category = entity.Read<EntityCategory>();
                                if (category.MaterialCategory == MaterialCategory.Vegetation)
                                {
                                    category.MaterialCategory = MaterialCategory.Mineral;
                                    entity.Write(category);
                                }
                            }
                        }

                        // Can't have entities overlap a heart so they have to be destroyed
                        foreach (var heartAabb in heartAabbsInLoadArea)
                        {
                            if (Helper.IsEntityInAabb(entity, heartAabb))
                            {
                                DestroyUtility.Destroy(Core.EntityManager, entity);
                                entity = Entity.Null;
                                break;
                            }
                        }

                        if (entity.Has<BlueprintData>())
                        {
                            var blueprintData = entity.Read<BlueprintData>();
                            blueprintData.Entity = entity;
                            entity.Write(blueprintData);
                        }

                        createdEntities[i + 1] = entity;
                    }
                }

                // Second pass modify all their components
                foreach (var i in entityGroupToLoad)
                {
                    var diff = schematic.entities[i];
                    var entity = createdEntities[i + 1];

                    if (entity.Equals(Entity.Null))
                        continue;

                    //Core.Log.LogInfo($"Modifying {i} which is {diff.prefab.LookupName()}");

                    ComponentSaver.ComponentSaver.ApplyComponentData(entity, diff.componentData, createdEntities);
                    ComponentSaver.ComponentSaver.ApplyRemovals(entity, diff.removals);
                }

                if (Time.realtimeSinceStartup - lastYieldTime > 0.05f)
                {
                    Core.Log.LogInfo($"{GetElapseTime():f4} Loaded {entitiesLoadedThisFrame} entities this frame for {100 * (float)entitiesLoaded.Count / (float)schematic.entities.Length:F1}% complete");
                    MessageUser($"Loading {100 * (float)entitiesLoaded.Count / (float)schematic.entities.Length:F1}% complete");
                    yield return new WaitForSeconds(1f);
                    entitiesLoadedThisFrame = 0;
                    lastYieldTime = Time.realtimeSinceStartup;
                }
            } while (entitiesLoaded.Count < schematic.entities.Length);

            Core.Log.LogInfo($"{GetElapseTime():f4} Finished Loading Schematic");
            var message = new FixedString512Bytes("Finished Loading Schematic");
            ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, userEntity.Read<User>(), ref message);
            Core.RespawnPrevention.AllowRespawns();

            if (PrefabGUIDConverter.missingPrefabs.Count > 0)
            {
                var sb = new StringBuilder("Missing prefabs that could be remapped\n");
                foreach(var missingPrefab in PrefabGUIDConverter.missingPrefabs.OrderBy(x => x))
                {
                    if (sb.Length + missingPrefab.Length > 500)
                    {
                        message = new FixedString512Bytes(sb.ToString());
                        ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, userEntity.Read<User>(), ref message);
                        sb.Clear();
                    }
                    sb.AppendLine(missingPrefab);
                }
                message = new FixedString512Bytes(sb.ToString());
                ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, userEntity.Read<User>(), ref message);
                PrefabGUIDConverter.missingPrefabs.Clear();
            }
        }

        public void SetFallbackCastleHeart(Entity charEntity, Entity castleHeartEntity, bool ownerDoors=false, bool ownerChests=false)
        {
            fallbackHeart[charEntity] = new HeartInfo
            {
                CastleHeart = castleHeartEntity,
                OwnerDoors = ownerDoors,
                OwnerChests = ownerChests,
            };
        }

        public void UseNeutralDoors(Entity charEntity)
        {
            GetFallbackCastleHeart(charEntity, out var castleHeartEntity, out var _, out var ownerChests);
            fallbackHeart[charEntity] = new HeartInfo
            {
                CastleHeart = castleHeartEntity,
                OwnerDoors = false,
                OwnerChests = ownerChests,
            };
        }

        public void UseOwnerDoors(Entity charEntity)
        {
            GetFallbackCastleHeart(charEntity, out var castleHeartEntity, out var _, out var ownerChests);
            fallbackHeart[charEntity] = new HeartInfo
            {
                CastleHeart = castleHeartEntity,
                OwnerDoors = true,
                OwnerChests = ownerChests,
            };
        }

        public void UseNeutralChests(Entity charEntity)
        {
            GetFallbackCastleHeart(charEntity, out var castleHeartEntity, out var ownerDoors, out var _);
            fallbackHeart[charEntity] = new HeartInfo
            {
                CastleHeart = castleHeartEntity,
                OwnerDoors = ownerDoors,
                OwnerChests = false,
            };
        }

        public void UseOwnerChests(Entity charEntity)
        {
            GetFallbackCastleHeart(charEntity, out var castleHeartEntity, out var ownerDoors, out var _);
            fallbackHeart[charEntity] = new HeartInfo
            {
                CastleHeart = castleHeartEntity,
                OwnerDoors = ownerDoors,
                OwnerChests = true,
            };
        }

        public bool IsNeutralTeam(Entity entityToCheck) => entityToCheck.Equals(neutralTeam);

        public void GetFallbackCastleHeart(Entity charEntity, out Entity castleHeartEntity, out bool ownerDoors, out bool ownerChests)
        {
            castleHeartEntity = Entity.Null;
            ownerDoors = true;
            ownerChests = true;

            if (fallbackHeart.TryGetValue(charEntity, out var heartInfo))
            {
                castleHeartEntity = heartInfo.CastleHeart;
                MakeHeartUsableEverywhere(castleHeartEntity);
                ownerDoors = heartInfo.OwnerDoors;
                ownerChests = heartInfo.OwnerChests;
                return;
            }

            if (charEntity.Has<TeamReference>())
            {
                var team = charEntity.Read<TeamReference>().Value;
                foreach (var allyEntries in Core.EntityManager.GetBuffer<TeamAllies>(team))
                {
                    var allyEntity = allyEntries.Value;
                    if (allyEntity.Has<CastleTeamData>())
                    {
                        castleHeartEntity = allyEntity.Read<CastleTeamData>().CastleHeart;
                        MakeHeartUsableEverywhere(castleHeartEntity);
                        break;
                    }
                }
            }

            static void MakeHeartUsableEverywhere(Entity heartEntity)
            {
                if (heartEntity == Entity.Null) return;

                if (heartEntity.Has<SyncBoundingBox>()) heartEntity.Remove<SyncBoundingBox>();
                if (!heartEntity.Has<SyncToUserBitMask>())
                {
                    heartEntity.Add<SyncToUserBitMask>();
                    heartEntity.Write(new SyncToUserBitMask()
                    {
                        Value = new UserBitMask128()
                        {
                            _Value = new int4(-1, -1, -1, -1)
                        }
                    });
                }
            }
        }

        private static Entity SpawnEntity(Entity userEntity, Vector3 translation, EntityData diff, Entity prefab)
        {
            var entity = Core.EntityManager.Instantiate(prefab);

            // Mark this entity as spawned with KindredSchematics
            entity.Add<PhysicsCustomTags>();

            if (diff.pos.HasValue)
            {
                if (!entity.Has<Translation>())
                    entity.Add<Translation>();
                entity.Write(new Translation { Value = diff.pos.Value + translation });
                if (entity.Has<LastTranslation>())
                    entity.Write(new LastTranslation { Value = diff.pos.Value + translation });
            }
            if (diff.rot.HasValue)
            {
                if (!entity.Has<Rotation>())
                    entity.Add<Rotation>();
                entity.Write(new Rotation { Value = diff.rot.Value });
            }

            int2 offset = new(Mathf.FloorToInt(translation.x * 2), Mathf.FloorToInt(translation.z * 2));
            if (diff.tilePos.HasValue)
            {
                if (!entity.Has<TilePosition>())
                    entity.Add<TilePosition>();
                entity.Write(new TilePosition { Tile = diff.tilePos.Value + offset });
            }

            if (diff.tileBoundsMin.HasValue && diff.tileBoundsMax.HasValue)
            {
                if (!entity.Has<TileBounds>())
                    entity.Add<TileBounds>();
                entity.Write(new TileBounds { Value = new() { Min = diff.tileBoundsMin.Value + offset, Max = diff.tileBoundsMax.Value + offset } });
            }

            return entity;
        }

        public bool ToggleClearingEntireArea(Entity userEntity)
        {
            if (usersClearingEntireArea.Contains(userEntity))
            {
                usersClearingEntireArea.Remove(userEntity);
                return false;
            }
            usersClearingEntireArea.Add(userEntity);
            return true;
        }

        public bool TogglePlacingOffGrid(Entity userEntity)
        {
            if (usersPlacingOffGrid.Contains(userEntity))
            {
                usersPlacingOffGrid.Remove(userEntity);
                return false;
            }
            usersPlacingOffGrid.Add(userEntity);
            return true;
        }
    }
}
