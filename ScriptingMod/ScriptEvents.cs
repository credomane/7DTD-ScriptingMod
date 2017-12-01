﻿namespace ScriptingMod
{
    public enum ScriptEvents
    {
        // Each member should have exactly ONE using, which is where the event handler is invoked

        animalDamaged,
        animalDied,
        chatMessage,
        chunkLoaded,
        chunkMapCalculated,
        chunkUnloaded,
        eacPlayerAuthenticated,
        eacPlayerKicked,
        playerDamaged, // suggested by Guppycur, StompyNZ, Xyth
        entityLoaded,
        entityUnloaded,
        gameAwake,
        gameShutdown,
        gameStartDone,
        gameStatsChanged,
        logMessageReceived,
        playerDied,
        playerDisconnected,
        playerLogin,
        playerSaveData,
        playerSpawnedInWorld,
        playerSpawning,
        serverRegistered,
        zombieDamaged, // suggested by Guppycur, StompyNZ, Xyth
        zombieDied,
    }
}