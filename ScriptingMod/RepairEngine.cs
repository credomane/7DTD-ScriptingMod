﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using ScriptingMod.Extensions;

namespace ScriptingMod
{
    [Flags]
    public enum RepairTasks
    {
        //                     |----------------------------------(max length: 120 char)----------------------------------------------------------------|
        [RepairTask('D', "Repair block density causing distorted terrain and falling through the world.")]
        BlockDensity            = 1,
        [RepairTask('L', "Fix death screen loop due to corrupt player files.")]
        DeathScreenLoop    = 2,
        [RepairTask('M', "Bring all stuck minibikes to the surface.")]
        StuckMinibikes     = 4,
        [RepairTask('P', "Fix corrupt power blocks and error message \"NullReferenceException at TileEntityPoweredTrigger.write ...\".")]
        CorruptPowerBlocks = 8,
        [RepairTask('R', "Reset locked respawn of biome zombies and animals, especially after using settime, bc-remove, or dj-regen. (EXPERIMENTAL)")]
        LockedBiomeRespawn = 16,

        None               = 0,
        Default            = ~0
    }

    internal class RepairEngine
    {
        /// <summary>
        /// Entities are allowed to wander this many chunks away from their initial 5x5 chunk area
        /// </summary>
        private const int EntitiySearchRadius = 4;

        /// <summary>
        /// Allows assigning a method that outputs status information to the console, e.g. SdtdConsole.Output
        /// </summary>
        public Action<string> ConsoleOutput;

        /// <summary>
        /// If set to true, problems will only be reported without fixing them
        /// </summary>
        public bool Simulate;

        /// <summary>
        /// Allows defining the problems to scan for, using binary flags (multiple possible)
        /// Default: RepairTasks.All
        /// </summary>
        public RepairTasks Tasks = RepairTasks.Default;

        /// <summary>
        /// Number of problems that this repair engine has found (and attempted fixing unless simulating) so far
        /// </summary>
        public int ProblemsFound;

        private Stopwatch _stopwatch;
        private int _scannedChunks;

        private string FoundOrRepaired => Simulate ? "Found" : "Repaired";

        /// <summary>
        /// For TileEntityPoweredTrigger objects this lists the TriggerTypes and which power item class are allowed together.
        /// Last updated: A16.2 b7
        /// Source: See TileEntityPoweredTrigger.CreatePowerItem()
        /// </summary>
        private static readonly Dictionary<PowerTrigger.TriggerTypes, Type> ValidTriggerTypes =
            new Dictionary<PowerTrigger.TriggerTypes, Type>
            {
                { PowerTrigger.TriggerTypes.Switch,        typeof(PowerTrigger) },
                { PowerTrigger.TriggerTypes.PressurePlate, typeof(PowerPressurePlate) },
                { PowerTrigger.TriggerTypes.TimerRelay,    typeof(PowerTimerRelay) },
                { PowerTrigger.TriggerTypes.Motion,        typeof(PowerTrigger) },
                { PowerTrigger.TriggerTypes.TripWire,      typeof(PowerTripWireRelay) }
            };

        private static readonly World World = GameManager.Instance.World;

        private static ulong _maxAllowedRespawnDelayCache;

        /// <summary>
        /// The cached maximum allowed respawn delay in world ticks for ChunkBiomeSpawnData entires.
        /// See: ChunkAreaBiomeSpawnData.SetRespawnLocked(..) and EntityPlayer.onSpawnStateChanged(..)
        /// </summary>
        private static ulong MaxAllowedRespawnDelay
        {
            get
            {
                if (_maxAllowedRespawnDelayCache == 0UL)
                {
                    _maxAllowedRespawnDelayCache = (ulong)(GamePrefs.GetInt(EnumGamePrefs.PlayerSafeZoneHours) * 1000);

                    foreach (var biome in World.Biomes.GetBiomeMap().Values)
                    {
                        if (biome == null)
                            continue;
                        var spawnEntityGroupList = BiomeSpawningClass.list[biome.m_sBiomeName];
                        if (spawnEntityGroupList == null)
                            continue;
                        foreach (var spawnEntityGroupData in spawnEntityGroupList.list)
                        {
                            if (spawnEntityGroupData == null)
                                continue;
                            if (_maxAllowedRespawnDelayCache < (ulong)spawnEntityGroupData.respawnDelayInWorldTime)
                                _maxAllowedRespawnDelayCache = (ulong)spawnEntityGroupData.respawnDelayInWorldTime;
                        }
                    }
                }
                return _maxAllowedRespawnDelayCache;
            }
        }

        public void Start()
        {
            if (Tasks == RepairTasks.None)
                throw new ApplicationException("No repair tasks set.");

            _stopwatch = new MicroStopwatch(true);

            LogAndOutput($"{(Simulate ? "Scan (without repair)" : "Repair")} for server problem(s) {GetTaskLetters(Tasks)} started.");

            // Scan chunks -> tile entities
            if (Tasks.HasFlag(RepairTasks.CorruptPowerBlocks) || Tasks.HasFlag(RepairTasks.LockedBiomeRespawn))
            {
                Output("Scanning all loaded chunks ...");
                foreach (var chunk in World.ChunkCache.GetChunkArrayCopySync())
                    RepairChunk(chunk);
            }

            // Scan players
            //if (Tasks.HasFlag(RepairTasks.DeathScreenLoop))
            //{
            //    Output("Scanning all players ...");
            //    // TODO
            //}

            LogAndOutput($"{(Simulate ? "Identified" : "Repaired")} {ProblemsFound} problem{(ProblemsFound != 1 ? "s" : "")} " +
                         $"in {_scannedChunks} chunk{(_scannedChunks != 1 ? "s" : "")}. [details in server log]");

            Log.Debug($"Repair engine done. Execution took {_stopwatch.ElapsedMilliseconds} ms.");
        }

        /// <summary>
        /// Returns all task letters of the given tasks flag combinations, e.g. "DLMPR" for RepairTasks.Default
        /// </summary>
        /// <param name="tasks">Binary flags of RepairTasks</param>
        /// <returns>Sorted uppercase letters as string</returns>
        public static string GetTaskLetters(RepairTasks tasks)
        {
            return Enum.GetValues(typeof(RepairTasks))
                .Cast<RepairTasks>()
                .Where(task => tasks.HasFlag(task))
                .Select(task => task.GetAttributeOfType<RepairTaskAttribute>())
                .Where(attr => attr != null)
                .OrderBy(attr => attr.Letter)
                .Aggregate("", (str, attr) => str + attr.Letter);
        }

        /// <summary>
        /// Scans the given chunk object for corrupt power blocks and optionally fixes them
        /// </summary>
        /// <param name="chunk">The chunk object; must be loaded and ready</param>
        private void RepairChunk([NotNull] Chunk chunk)
        {
            if (Tasks.HasFlag(RepairTasks.LockedBiomeRespawn))
                RepairChunkRespawn(chunk);

            foreach (var tileEntity in chunk.GetTileEntities().Values.ToList())
                RepairTileEntity(tileEntity);

            _scannedChunks++;
        }

        private void RepairChunkRespawn([NotNull] Chunk chunk)
        {
            // Only area master chunks (every 5th chunk) control respawn with their chustom chunk data (ChunkBiomeSpawnData)
            if (!chunk.IsAreaMaster())
                return;

            // No ChunkBiomeSpawnData, no problem; that's the default
            var spawnData = chunk.GetChunkBiomeSpawnData();
            if (spawnData == null)
                return;

            Log.Debug($"Scanning area master {chunk} with spawn data: {spawnData}");
            foreach (var groupName in spawnData.GetEntityGroupNames().ToList())
            {
                RepairLongRespawnLock(spawnData, groupName);
                RepairLostEntities(spawnData, groupName);
            }
        }

        private void RepairLongRespawnLock([NotNull] ChunkAreaBiomeSpawnData spawnData, string groupName)
        {
            // Respawn is allowed to be locked for max 7 in-game days (MaxAllowedRespawnDelay)
            ulong respawnLockedUntil = spawnData.GetRespawnLockedUntilWorldTime(groupName);
            if (respawnLockedUntil <= World.worldTime + MaxAllowedRespawnDelay)
                return;

            // Problem: Respawn locked is too long; possibly due to modified worldtime with "settime"

            ProblemsFound++;
            if (!Simulate)
            {
                spawnData.ClearRespawnLocked(groupName);
                spawnData.chunk.isModified = true;
            }
            WarningAndOutput($"{FoundOrRepaired} respawn of {groupName} locked for {respawnLockedUntil / 1000 / 24} game days in area master {spawnData.chunk}.");
        }

        private void RepairLostEntities([NotNull] ChunkAreaBiomeSpawnData spawnData, string groupName)
        {
            // No need to check if no entities are locked
            int registeredEntities = spawnData.GetEntitiesSpawned(groupName);
            if (registeredEntities <= 0)
                return;

            // Lock with timeout is handled by RepairLongRespawnLock(..)
            ulong respawnLockedUntil = spawnData.GetRespawnLockedUntilWorldTime(groupName);
            if (respawnLockedUntil > 0)
                return;

            // Less or equal entities are registered than online is normal;
            // less registered can occur when zombies from other chunks wander in
            var spawnedEntities = CountSpawnedEntities(EnumSpawnerSource.Biome, spawnData.chunk.Key, groupName);
            if (registeredEntities <= spawnedEntities)
                return;

            int lostEntities = registeredEntities - spawnedEntities;

            // Ignore missing zombies when it could come from surrounding chunks not loaded
            if (!AllChunksLoaded(spawnData.chunk, EntitiySearchRadius))
            {
                Log.Debug($"Ignoring {lostEntities} lost {groupName} in area master {spawnData.chunk} " +
                          $"because not all it's 5x5 chunks + {EntitiySearchRadius} chunks around them are loaded.");
                return;
            }

            // Problem: Zombies are registered in the chunk that cannot be found alive anywhere; they might have disappeared or wandered too far off

            ProblemsFound++;
            if (!Simulate)
            {
                SetEntitiesSpawned(spawnData, groupName, spawnedEntities);
            }
            WarningAndOutput($"{FoundOrRepaired} respawn of {groupName} locked because of {lostEntities} lost " +
                             $"{(lostEntities == 1 ? "entity" : "entities")} in area master {spawnData.chunk}.");
        }

        /// <summary>
        /// The the entitiesSpawned private variable in spawnData for the given groupName to the new value
        /// by repeatedly calling Inc.. or Dec.. methods. This is faster and less complicated than reflection
        /// </summary>
        /// <param name="spawnData">Class to modify</param>
        /// <param name="groupName">Spawn group name to change entry for</param>
        /// <param name="entitiesSpawned">New value</param>
        private static void SetEntitiesSpawned([NotNull] ChunkAreaBiomeSpawnData spawnData, string groupName, int entitiesSpawned)
        {
            int delta = entitiesSpawned - spawnData.GetEntitiesSpawned(groupName);
            for (int i = 0; i < delta; i++)
                spawnData.IncEntitiesSpawned(groupName);
            for (int i = 0; i > delta; i--)
                spawnData.DecEntitiesSpawned(groupName);
        }

        /// <summary>
        /// Find/fix problems with TileEntityPowered objects and their PowerItems,
        /// which may caus NRE at TileEntityPoweredTrigger.write and other problems
        /// </summary>
        /// <param name="tileEntity">Tile entity to repair; currently only TileEntityPowered type is scanned/repaired</param>
        private void RepairTileEntity([NotNull] TileEntity tileEntity)
        {
            var powered = tileEntity as TileEntityPowered;
            if (powered != null && !IsValidTileEntityPowered(powered))
            {
                ProblemsFound++;

                if (!Simulate)
                    RecreateTileEntity(tileEntity);

                LogAndOutput($"{FoundOrRepaired} corrupt power block at {tileEntity.ToWorldPos()} in {tileEntity.GetChunk()}.");
            }
        }

        /// <summary>
        /// Returns the number of active entities in the world, filtered by the given spawner source, sourceChunkKey, and sourceEntityGroup
        /// </summary>
        /// <param name="spawnerSource"></param>
        /// <param name="spawnerSourceChunkKey"></param>
        /// <param name="spawnerSourceEntityGroupName"></param>
        /// <returns>The number of found entities; can be 0</returns>
        private static int CountSpawnedEntities(EnumSpawnerSource spawnerSource, long spawnerSourceChunkKey, string spawnerSourceEntityGroupName)
        {
            return World.Entities.list.Count(
                e => e.GetSpawnerSource() == spawnerSource
                     && e.GetSpawnerSourceChunkKey() == spawnerSourceChunkKey
                     && e.GetSpawnerSourceEntityGroupName() == spawnerSourceEntityGroupName);
        }

        /// <summary>
        /// Returns true if all chunks that belong to the given area master chunk, extended by the given value, are currently loaded.
        /// </summary>
        /// <param name="areaMasterChunk">The area master chunk to use as basis</param>
        /// <param name="extendBy">Number of chunks to extend the check area in all directions, additionally to the 5x5 area master</param>
        private static bool AllChunksLoaded([NotNull] Chunk areaMasterChunk, int extendBy)
        {
            if (!areaMasterChunk.IsAreaMaster())
                throw new ArgumentException("Given chunk is not an area master chunk.", nameof(areaMasterChunk));

            lock (World.ChunkCache.GetSyncRoot())
            {
                for (int x = areaMasterChunk.X - extendBy; x < areaMasterChunk.X + Chunk.cAreaMasterSizeChunks + extendBy; x++)
                    for (int z = areaMasterChunk.Z - extendBy; z < areaMasterChunk.Z + Chunk.cAreaMasterSizeChunks + extendBy; z++)
                        if (!World.ChunkCache.ContainsChunkSync(WorldChunkCache.MakeChunkKey(x, z, areaMasterChunk.ClrIdx)))
                            return false;
            }
            return true;
        }

        /// <summary>
        /// Returns false if the given tile entity has an invalid PowerItem attached; true otherwise
        /// </summary>
        public static bool IsValidTileEntityPowered([NotNull] TileEntityPowered te)
        {
            var teType = te.GetType();
            var pi = te.GetPowerItem();

            // Can't check what's not there. That's ok, some powered blocks (e.g. lamps) don't have a power item until connected.
            if (pi == null)
                return true;

            var piType = pi.GetType();

            var teTrigger = te as TileEntityPoweredTrigger;
            if (teTrigger != null)
            {
                // Trigger must be handled differently, because there are multiple possible power items for one TileEntityPoweredTriger,
                // and the PowerItemType is sometimes just (incorrectly) "PowerSource" when the TriggerType determines the *real* power type.

                // CHECK 1: Power item should be of type PowerTrigger if this is a TileEntityPoweredTrigger
                var piTrigger = pi as PowerTrigger;
                if (piTrigger == null)
                {
                    Log.Debug($"[{te.ToWorldPos()}] {teType} should have power item \"PowerTrigger\" or some descendant of it, but has power item \"{piType}\".");
                    return false;
                }

                // CHECK 2: PowerItemType should match the actual power item's object type, or be at least "PowerSource",
                // because TileEntityPoweredTrigger sometimes has the (incorrect) default PowerItemType "PowerSource" value
                // and only TriggerType is reliable. It "smells" but we have to accept it.
                if (te.PowerItemType != pi.PowerItemType && te.PowerItemType != PowerItem.PowerItemTypes.Consumer)
                {
                    Log.Debug($"[{te.ToWorldPos()}] {teType}.PowerItemType=\"{te.PowerItemType}\" doesn't match with {piType}.PowerItemType=\"{pi.PowerItemType}\" " +
                              $"and is also not the default \"{PowerItem.PowerItemTypes.Consumer}\".");
                    return false;
                }

                // CHECK 3: TriggerType and actual power item type should be compatible
                var expectedClass = ValidTriggerTypes.GetValue(teTrigger.TriggerType);
                if (expectedClass == null)
                    Log.Warning($"Unknown enum value PowerTrigger.TriggerTypes.{teTrigger.TriggerType} found.");
                else if (piType != expectedClass)
                {
                    Log.Debug($"[{te.ToWorldPos()}] {teType}.TriggerType=\"{teTrigger.TriggerType}\" doesn't fit together with power item \"{piType}\". " +
                              $"A {expectedClass} was expected.");
                    return false;
                }

                // CHECK 4: Tile entity's TriggerType and power items's TriggerType should match
                if (teTrigger.TriggerType != piTrigger.TriggerType)
                {
                    Log.Debug($"[{te.ToWorldPos()}] {teType}.TriggerType=\"{teTrigger.TriggerType}\" doesn't match with {piType}.PowerItemType=\"{piTrigger.TriggerType}\".");
                    return false;
                }
            }
            else
            {
                // CHECK 5: For all non-trigger tile entities, the power item type must match with the actual object
                if (te.PowerItemType != pi.PowerItemType)
                {
                    Log.Debug($"[{te.ToWorldPos()}] {teType}.PowerItemType=\"{te.PowerItemType}\" doesn't match with {piType}.PowerItemType=\"{pi.PowerItemType}\".");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Deletes the given tile entity from it's chunk and creates a new one based on the tile entity type
        /// </summary>
        private static void RecreateTileEntity([NotNull] TileEntity tileEntity)
        {
            var chunk = tileEntity.GetChunk();

            // Prevent further errors on client updates; crucial when removing power item!
            tileEntity.SetDisableModifiedCheck(true);

            // Remove corrupt tile entity
            chunk.RemoveTileEntity(World, tileEntity);

            // Remove power item
            var tePowered = tileEntity as TileEntityPowered;
            var powerItem = tePowered?.GetPowerItem();
            if (powerItem != null)
                PowerManager.Instance.RemovePowerNode(powerItem);

            // Create new tile entity
            var newTileEntity = TileEntity.Instantiate(tileEntity.GetTileEntityType(), chunk);
            newTileEntity.localChunkPos = tileEntity.localChunkPos;
            chunk.AddTileEntity(newTileEntity);

            // Recreate power item if necessary
            var newPowered = newTileEntity as TileEntityPowered;
            if (newPowered != null)
            {
                // Restore old PowerItemType and TriggerType values
                if (tePowered != null)
                    newPowered.PowerItemType = tePowered.PowerItemType;

                // fancy new C#7 syntax, isn't it? :)
                if (tileEntity is TileEntityPoweredTrigger teTrigger && newPowered is TileEntityPoweredTrigger newTrigger)
                    newTrigger.TriggerType = teTrigger.TriggerType;

                // Create power item according to PowerItemType and TriggerType
                newPowered.InitializePowerData();

                // Wires to the corrupt block are cut and not restored. We could try to reattach everything, but meh...
            }

            var newPowerItem = newPowered?.GetPowerItem();
            Log.Debug($"[{tileEntity.ToWorldPos()}] Replaced old {tileEntity.GetType()} with new {newTileEntity.GetType()}" +
                      $"{(newPowerItem != null ? " and new power item " + newPowerItem.GetType() : "")}.");
        }

        private void WarningAndOutput(string msg)
        {
            Log.Warning(msg);
            Output(msg);
        }

        private void LogAndOutput(string msg)
        {
            Log.Out(msg);
            Output(msg);
        }

        private void Output(string msg)
        {
            if (ConsoleOutput == null)
                return;
            ConsoleOutput(msg);
        }
    }
}