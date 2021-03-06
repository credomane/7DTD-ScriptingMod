﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Harmony;
using JetBrains.Annotations;
using ScriptingMod.ScriptEngines;
using ScriptingMod.Tools;

namespace ScriptingMod.Patches
{
    [HarmonyPatch(typeof(Chunk), "AddEntityToChunk")]
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class PlayerEnteredChunk
    {

        public static bool Prepare()
        {
            if (!CommandTools.IsAnyEventActive(ScriptEvent.playerEnteredChunk))
            {
                Log.Debug($"Patch {nameof(PlayerEnteredChunk)} is disabled.");
                return false;
            }

            Log.Out($"Injecting event tracker {nameof(PlayerEnteredChunk)} ...");
            return true;
        }

        public static bool Prefix(Chunk __instance, Entity _entity)
        {
            Log.Debug($"Executing patch prefix {nameof(PlayerEnteredChunk)} ...");

            if (_entity is EntityPlayer player)
            {
                CommandTools.InvokeScriptEvents(ScriptEvent.playerEnteredChunk, () => new PlayerEnteredChunkEventArgs()
                {
                    newChunk   = new Vector2xz(__instance.X, __instance.Z),
                    oldChunk   = new Vector2xz(_entity.chunkPosAddedEntityTo.x, _entity.chunkPosAddedEntityTo.z),
                    position   = player.GetBlockPosition(),
                    clientInfo = ConnectionManager.Instance?.GetClientInfoForEntityId(player.entityId),
                });
            }
            return true;
        }
    }
}
