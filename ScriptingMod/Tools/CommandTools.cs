﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JetBrains.Annotations;
using ScriptingMod.Commands;
using ScriptingMod.Exceptions;
using ScriptingMod.Extensions;
using ScriptingMod.ScriptEngines;
using UnityEngine;

namespace ScriptingMod.Tools
{
    using CommandObjectPair = NonPublic.SdtdConsole.CommandObjectPair;

    internal static class CommandTools
    {
        private static readonly CommandObjectPairComparer _commandObjectPairComparer = new CommandObjectPairComparer();
        private static CommandObjectComparer _commandObjectComparer = new CommandObjectComparer();
        private static FileSystemWatcher _scriptsWatcher;
        private static bool _scriptsChangedRunning;
        private static object _scriptsChangedLock = new object();

        /// <summary>
        /// Dictionary of event => [NotNull] List of script filePaths
        /// </summary>
        private static Dictionary<ScriptEvents, List<string>> _events = new Dictionary<ScriptEvents, List<string>>();

        /// <summary>
        /// Subscribes to additional scripting events that are not called directly;
        /// MUST be called in GameStartDone or later because World is used
        /// </summary>
        public static void InitEvents()
        {
            // A lot of other methods are already calling InvokeScriptEvents(..) directly.
            // Here are just the ones that need to be attached to actual events.
            // See enum ScriptEvents and it's usages for a full list of supported scripting events.

#region ScriptingMod events

            // Called when a player got kicked due to failed EAC check
            EacTools.PlayerKicked += delegate (ClientInfo clientInfo, GameUtils.KickPlayerData kickPlayerData)
            {
                Log.Debug($"Event \"{typeof(EacTools)}.{nameof(EacTools.PlayerKicked)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.eacPlayerKicked, new { clientInfo, kickPlayerData });
            };

            // Called when a player successfully passed the EAC check
            EacTools.AuthenticationSuccessful += delegate (ClientInfo clientInfo)
            {
                Log.Debug($"Event \"{typeof(EacTools)}.{nameof(EacTools.AuthenticationSuccessful)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.eacPlayerAuthenticated, new { clientInfo });
            };

#endregion

#region Steam events

            //var steam = Steam.Instance ?? throw new NullReferenceException("Steam not ready.");

            // Called first when a player is connecting before any authentication
            // Removed because Api.PlayerLogin is also called before authentication and also contains clientInfo.networkPlayer
            //steam.PlayerConnectedEv += delegate(NetworkPlayer networkPlayer)
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.PlayerConnectedEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamPlayerConnected.ToString(), networkPlayer });
            //};

            // Called first when the server is about to shut down
            // Removed because it doesn't add much value
            //steam.ApplicationQuitEv += delegate()
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.ApplicationQuitEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamApplicationQuit.ToString() });
            //};

            // Called right before the game process ends as last event of shutdown
            // Removed because it doesn't add much value
            //steam.DestroyEv += delegate()
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.DestroyEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamDestroy.ToString() });
            //};

            // Called after the game has disconnected from Steam servers and shuts down
            // Removed because it doesn't add much value
            //steam.DisconnectedFromServerEv += delegate(NetworkDisconnection reason)
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.DisconnectedFromServerEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamDisconnectedFromServer.ToString(), reason });
            //};

            // Invoked on every tick
            // Removed because too big performance impact for scripting event
            //steam.UpdateEv += delegate ()
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.UpdateEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamUpdate.ToString() });
            //};

            // Invoked on every tick
            // Removed because too big performance impact for scripting event
            //steam.LateUpdateEv += delegate ()
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.LateUpdateEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamLateUpdate.ToString() });
            //};

            // Called after a player disconnected, a chat message was distributed, and all associated game data has been unloaded
            // Removed because it's similar to "playerDisconnected" and the passed networkPlayer cannot be used on a disconnected client anyway
            //steam.PlayerDisconnectedEv += delegate (NetworkPlayer networkPlayer)
            //{
            //    Log.Debug($"Event \"{typeof(Steam)}.{nameof(Steam.PlayerDisconnectedEv)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.steamPlayerDisconnected.ToString(), networkPlayer });
            //};

            // Called when the server was registered with Steam and announced to the master servers (also done for non-public dedicated servers)
            Steam.Masterserver.Server.AddEventServerRegistered(delegate()
            {
                Log.Debug($"Event \"{typeof(MasterServerAnnouncer)}.ServerRegistered (Event_0)\" invoked.");
                InvokeScriptEvents(ScriptEvents.serverRegistered, new { masterServerAnnouncer = Steam.Masterserver.Server });
            });

#endregion

#region UnityEngine.Application events

            // Called when main Unity thread logs an error message
            Application.logMessageReceived += delegate (string condition, string trace, LogType logType)
            {
                Log.Debug($"Event \"{typeof(Application)}.{nameof(Application.logMessageReceived)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.logMessageReceived, new { condition, trace, logType });
            };

            // Called when ANY Unity thread logs an error message
            Application.logMessageReceivedThreaded += delegate (string condition, string trace, LogType logType)
            {
                Log.Debug($"Event \"{typeof(Application)}.{nameof(Application.logMessageReceivedThreaded)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.logMessageReceived, new { condition, trace, logType });
            };

#endregion

#region GameManager events

            // Called on shutdown when the world becomes null. Not called on startup apparently.
            // Removed because not useful
            //GameManager.Instance.OnWorldChanged += delegate (World world_)
            //{
            //    Log.Debug($"Event \"{typeof(GameManager)}.{nameof(GameManager.OnWorldChanged)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.gameManagerWorldChanged.ToString(), world = world_ });
            //}; 

#endregion

#region World events

            var world = GameManager.Instance.World ?? throw new NullReferenceException(Resources.ErrorWorldNotReady);

            // Called when any entity (zombie, item, air drop, player, ...) is spawned in the world, both loaded and newly created
            world.EntityLoadedDelegates += delegate (Entity entity)
            {
                Log.Debug($"Event \"{typeof(World)}.{nameof(World.EntityLoadedDelegates)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.entityLoaded, new { entity });
            };

            // Called when any entity (zombie, item, air drop, player, ...) disappears from the world, e.g. it got killed, picked up, despawned, logged off, ...
            world.EntityUnloadedDelegates += delegate (Entity entity, EnumRemoveEntityReason reason)
            {
                Log.Debug($"Event \"{typeof(World)}.{nameof(World.EntityUnloadedDelegates)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.entityUnloaded, new { entity, reason });
            };

            // Called when chunks change display status, i.e. either get displayed or stop being displayed.
            // chunkLoaded   -> Called when a chunk is loaded into the game engine because a player needs it. Called frequently - use with care!
            // chunkUnloaded -> Called when a chunk is unloaded from the game engine because it is not used by any player anymore. Called frequently - use with care!
            world.ChunkCache.OnChunkVisibleDelegates += delegate (long chunkKey, bool displayed)
            {
                // No logging to avoid spam
                //Log.Debug($"Event \"{typeof(ChunkCluster)}.{nameof(ChunkCluster.OnChunkVisibleDelegates)}\" invoked. (displayed={displayed}).");
                InvokeScriptEvents(displayed ? ScriptEvents.chunkLoaded : ScriptEvents.chunkUnloaded, new { chunkKey });
            };

            // Called on shutdown when the chunkCache is cleared; idx remains 0 tho. Not called on startup apparently.
            // Removed because not useful
            //world.ChunkClusters.ChunkClusterChangedDelegates += delegate (int chunkClusterIndex)
            //{
            //    Log.Debug($"Event \"{typeof(ChunkClusterList)}.{nameof(ChunkClusterList.ChunkClusterChangedDelegates)}\" invoked.");
            //    InvokeScriptEvents(new { type = ScriptEvents.chunkClusterChanged.ToString(), chunkClusterIndex });
            //};

#endregion

#region Other events

            // Called when game stats change including EnemyCount and AnimalCount, so it's called frequently. Use with care!
            GameStats.OnChangedDelegates += delegate(EnumGameStats gameState, object newValue)
            {
                // No logging to avoid spam
                // Log.Debug($"Event \"{typeof(GameStats)}.{nameof(GameStats.OnChangedDelegates)}\" invoked.");
                InvokeScriptEvents(ScriptEvents.gameStatsChanged, new { gameState, newValue});
            };

            #endregion

            // -------- TODO: Events to explore further --------
            // - MapVisitor - needs patching to attach to always newly created object; use-case questionable
            // - AIWanderingHordeSpawner.HordeArrivedDelegate hordeArrivedDelegate_0

            // ----------- TODO: More event ideas --------------
            // - Geofencing...trigger event when a player or zombie gets into a predefined area.
            // - Trigger server events on quest progress/completion. - So server admins could award questing further, or even unlock account features like forum access on quest completions.
            // - Event for Level-up
            // - Event for Explosions (TNT, dynamite, fuel barrel)
            // - Event for large collapses (say more than 50 blocks)
            // - Event for destruction of a car(e.g.to spawn a new car somewhere else)
            // - Event for idling more than X minutes
            // - Event for blacing bed
            // - Event for placing LCB
            // - Event on zombie/entity proximity (triggered when a player gets or leaves withing reach of X meters of a zombie) [Xyth]
            // - Exploring of new land
            // - Bloodmoon starting/ending
            // - Item was dropped [Xyth]
            // - Item durability hits zero [Xyth]
            // - Screamer spawned for a chunk/player/xyz [kenyer]
            // - AirDrop spawned
            // - Player banned
            // - Player unbanned
            // - Player died [Xyth]
            // - New Player connected for first time
            // - Events for ScriptingMod things
            // - Command that triggers when someone is in the air for more than X seconds, to catch hackers [war4head]

            // --------- Events never used on dedicated server ----------
            // - Steam.ConnectedToServerEv
            // - Steam.FailedToConnectEv
            // - Steam.ServerInitializedEv
            // - GameManager.Instance.OnLocalPlayerChanged
            // - World.OnWorldChanged
            // - ChunkCluster.OnChunksFinishedDisplayingDelegates
            // - ChunkCluster.OnChunksFinishedLoadingDelegates
            // - MapObjectManager.ChangedDelegates
            // - ServerListManager.GameServerDetailsEvent
            // - MenuItemEntry.ItemClicked
            // - LocalPlayerManager.*
            // - Inventory.OnToolbeltItemsChangedInternal
            // - BaseObjective.ValueChanged
            // - UserProfile.*
            // - CraftingManager.RecipeUnlocked
            // - QuestJournal.* (from EntityPlayer.QuestJournal)
            // - QuestEventManager.*
            // - UserProfileManager.*

            Log.Out("Subscribed to all relevant game events.");
        }

        public static void InitScripts()
        {
            var scripts = Directory.GetFiles(Constants.ScriptsFolder, "*.*", SearchOption.AllDirectories)
                .Where(s => s.EndsWith(LuaEngine.FileExtension, StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(JsEngine.FileExtension, StringComparison.OrdinalIgnoreCase));

            foreach (string script in scripts)
            {
                var filePath = script; // Needed prior C# 5.0 as closure
                var fileRelativePath = FileTools.GetRelativePath(filePath, Constants.ScriptsFolder);
                var fileName = Path.GetFileName(filePath);

                if (fileName.StartsWith("_"))
                {
                    Log.Out($"Script file {fileRelativePath} is ignored because it starts with underscore.");
                    continue;
                }

                Log.Debug($"Loading script {fileRelativePath} ...");

                try
                {
                    bool scriptUsed = false;
                    var scriptEngine = ScriptEngine.GetInstance(Path.GetExtension(filePath));
                    var metadata = scriptEngine.LoadMetadata(filePath);

                    // Register commands
                    var commandNames = metadata.GetValue("commands", "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (commandNames.Length > 0)
                    {
                        scriptUsed = true;
                        var description       = metadata.GetValue("description", "");
                        var help              = metadata.GetValue("help", null);
                        var defaultPermission = metadata.GetValue("defaultPermission").ToInt() ?? 0;
                        var action            = new DynamicCommandHandler((p, si) => scriptEngine.ExecuteCommand(filePath, p, si));
                        var commandObject     = new DynamicCommand(commandNames, description, help, defaultPermission, action);
                        AddCommand(commandObject);
                        Log.Out($"Registered command{(commandNames.Length == 1 ? "" : "s")} \"{commandNames.Join(" ")}\" from script {fileRelativePath}.");
                    }

                    // Register events
                    var eventNames = metadata.GetValue("events", "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (eventNames.Length > 0)
                    {
                        scriptUsed = true;
                        foreach (var eventName in eventNames)
                        {
                            ScriptEvents eventType;
                            try
                            {
                                // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                                eventType = (ScriptEvents)Enum.Parse(typeof(ScriptEvents), eventName);
                            }
                            catch (Exception)
                            {
                                Log.Warning($"Event \"{eventName}\" in script {fileRelativePath} is unknown and will be ignored.");
                                continue;
                            }

                            if (!_events.ContainsKey(eventType))
                                _events[eventType] = new List<string>();
                            _events[eventType].Add(filePath);
                        }
                        Log.Out($"Registered event{(eventNames.Length == 1 ? "" : "s")} \"{eventNames.Join(" ")}\" from script {fileRelativePath}.");
                    }

                    if (!scriptUsed)
                    {
                        Log.Out($"Script file {fileRelativePath} is ignored because it defines neither command names nor events.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not load command script {fileRelativePath}: {ex}");
                }
            }

            SaveChanges();

            Log.Debug("All script commands added.");
        }

        public static void InitScriptsMonitoring()
        {
            try
            {
                _scriptsWatcher = new FileSystemWatcher(Constants.ScriptsFolder);
                _scriptsWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                _scriptsWatcher.IncludeSubdirectories = true;
                _scriptsWatcher.Changed += ScriptsChanged;
                _scriptsWatcher.Created += ScriptsChanged;
                _scriptsWatcher.Deleted += ScriptsChanged;
                _scriptsWatcher.Renamed += ScriptsChanged;
                _scriptsWatcher.EnableRaisingEvents = true;
                Log.Out("Monitoring of script folder changes activated.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not initialize monitoring of scripting folder. Script file changes will not be detected. - " + ex);
            }
        }

        public static void InvokeScriptEvents(ScriptEvents eventType, [CanBeNull] object eventArgs)
        {
            // TrackInvocation(eventType, eventArgs);

            var sw = new MicroStopwatch(true);

            var contains = _events.ContainsKey(eventType);
            Log.Debug("Searching for script event took " + sw.ElapsedMicroseconds + " µs.");

            if (!contains)
                return;

            Log.Debug($"Invoking script event \"{eventType}\" ...");

            foreach (var filePath in _events[eventType])
            {
                var scriptEngine = ScriptEngine.GetInstance(Path.GetExtension(filePath));
                scriptEngine.ExecuteEvent(filePath, eventType, eventArgs);
            }
        }

        /// <summary>
        /// Track when and how this event was invoked first time;
        /// Only for development to learn if and when events are called.
        /// </summary>
        /// <param name="eventType"></param>
        /// <param name="eventArgs"></param>
        [Conditional("DEBUG")]
        private static void TrackInvocation(ScriptEvents eventType, object eventArgs)
        {
#if DEBUG
            var invocationLog = Environment.NewLine +
                                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                                Environment.StackTrace + Environment.NewLine +
                                Dumper.Dump(eventArgs, 1).TrimEnd();

            var invokedEvent = PersistentData.Instance.InvokedEvents.FirstOrDefault(ie => ie.EventName == eventType.ToString());

            if (invokedEvent == null)
            {
                invokedEvent = new PersistentData.InvokedEvent()
                {
                    EventName = eventType.ToString(),
                    FirstCall = invocationLog.Indent(8) + Environment.NewLine + new string(' ', 6),
                    LastCalls = new List<string>()
                };
                PersistentData.Instance.InvokedEvents.Add(invokedEvent);
            }

            // Rotate last 10 call logs with newest on top
            if (invokedEvent.LastCalls.Count == 10)
                invokedEvent.LastCalls.RemoveAt(invokedEvent.LastCalls.Count - 1);
            invokedEvent.LastCalls.Insert(0, invocationLog.Indent(10) + Environment.NewLine + new string(' ', 8));

            PersistentData.Instance.SaveLater();
#endif
        }

        /// <summary>
        /// Unload all our dynamic commands from the game
        /// </summary>
        private static void UnloadCommands()
        {
            var commandObjects = SdtdConsole.Instance.GetCommandObjects();
            for (int i = commandObjects.Count - 1; i >= 0; i--)
            {
                if (commandObjects.ElementAt(i) is DynamicCommand)
                    commandObjects.RemoveAt(i);
            }

            var commandObjectPairs = SdtdConsole.Instance.GetCommandObjectPairs();
            for (int i = commandObjectPairs.Count - 1; i >= 0; i--)
            {
                if (commandObjectPairs.ElementAt(i).CommandObject is DynamicCommand)
                    commandObjectPairs.RemoveAt(i);
            }

            SaveChanges();

            // Clear out attached scripts
            _events.Clear();

            Log.Out("Unloaded all scripting commands.");
        }

        private static void ScriptsChanged(object sender, FileSystemEventArgs args)
        {
            // Allow only one simultaneous event and skip all others
            lock (_scriptsChangedLock)
            {
                if (_scriptsChangedRunning)
                    return;
                _scriptsChangedRunning = true;
            }

            ThreadManager.AddSingleTask(info =>
            {
                try
                {
                    Log.Out("Changes in scripts folder detected. Reloading commands ...");

                    // Let all other associated events pass by
                    Thread.Sleep(500);

                    // Reload commands
                    UnloadCommands();
                    InitScripts();
                }
                catch (Exception ex)
                {
                    Log.Error("Error occured while changes in script folder were processed: " + ex);
                }
                finally
                {
                    _scriptsChangedRunning = false;
                }
            });
        }

        /// <summary>
        /// Registers the given command object with it's command names into the Console.
        /// The command object or command names must not already exist in the console.
        /// To make all command changes persistent, SaveChanges() must be called afterwards.
        /// Adapted from: SdtdConsole.RegisterCommands
        /// </summary>
        /// <param name="commandObject"></param>
        private static void AddCommand(DynamicCommand commandObject)
        {
            if (commandObject == null)
                throw new ArgumentNullException(nameof(commandObject));

            var commands = commandObject.GetCommands();

            if (commands == null || commands.Length == 0 || commands.All(string.IsNullOrEmpty))
                throw new ArgumentException("No command name(s) defined.");

            if (SdtdConsole.Instance.GetCommandObjects().Contains(commandObject))
                throw new ArgumentException($"The command object \"{commands.Join(" ")}\" already exists and cannot be registered twice.");

            foreach (string command in commands)
            {
                if (string.IsNullOrEmpty(command))
                    continue;

                if (CommandExists(command))
                    throw new ArgumentException($"The command \"{command}\" already exists and cannot be registered twice.");

                var commandObjectPair = new CommandObjectPair(command, commandObject);
                AddSortedCommandObjectPair(commandObjectPair);
            }

            AddCommandObjectSorted(commandObject);
        }

        private class CommandObjectComparer : IComparer<IConsoleCommand>
        {
            [SuppressMessage("ReSharper", "PossibleNullReferenceException")]
            public int Compare(IConsoleCommand o1, IConsoleCommand o2)
            {
                return string.Compare(o1.GetCommands()[0], o2.GetCommands()[0], StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Inserts a new CommandObject in the list at the position sorted by the first command name.
        /// See: https://stackoverflow.com/a/12172412/785111
        /// </summary>
        /// <param name="item"></param>
        private static void AddCommandObjectSorted(IConsoleCommand item)
        {
            var commandObjects = SdtdConsole.Instance.GetCommandObjects();
            var index = commandObjects.BinarySearch(item, _commandObjectComparer);
            if (index < 0) index = ~index;
            commandObjects.Insert(index, item);
            //Log.Debug($"Inserted new command object at index {index} of {commandObjects.Count-1}.");
        }

        private class CommandObjectPairComparer : IComparer<CommandObjectPair>
        {
            public int Compare(CommandObjectPair o1, CommandObjectPair o2)
            {
                return string.Compare(o1.Command, o2.Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Inserts a new CommandObjectPair object in the list at the position sorted by the command name
        /// See: https://stackoverflow.com/a/12172412/785111
        /// </summary>
        /// <param name="item">An object of struct type SdtdConsole.OL</param>
        private static void AddSortedCommandObjectPair(CommandObjectPair item)
        {
            var commandObjectPairs = SdtdConsole.Instance.GetCommandObjectPairs();
            var index = Array.BinarySearch(commandObjectPairs.ToArray(), item, _commandObjectPairComparer);
            if (index < 0) index = ~index;
            commandObjectPairs.Insert(index, item);
            //Log.Debug($"Inserted new command object pair at index {index} of {commandObjectPairs.Count-1}.");
        }

        private static void SaveChanges()
        {
            Log.Debug("Updating readonly copy of command list ...");
            SdtdConsole.Instance.SetCommandObjectsReadOnly(new ReadOnlyCollection<IConsoleCommand>(SdtdConsole.Instance.GetCommandObjects()));
            Log.Debug("Saving changes to commands and permissions to disk ...");
            GameManager.Instance.adminTools.Save();
        }

        private static bool CommandExists(string command)
        {
            return SdtdConsole.Instance.GetCommandObjectPairs().Any(pair => command.Equals(pair.Command, StringComparison.OrdinalIgnoreCase));
        }

        public static void HandleCommandException(Exception ex)
        {
            if (ex is FriendlyMessageException)
            {
                Log.Debug(ex.Message);
                SdtdConsole.Instance.Output(ex.Message);
            }
            else
            {
                Log.Exception(ex);
                SdtdConsole.Instance.Output(string.Format(Resources.ErrorDuringCommand, ex.Message));
            }
        }

        /// <summary>
        /// Parses two integer coordinates from the given position in the parameter list.
        /// </summary>
        /// <returns>The vector with the two values in x an z, y is always 0.</returns>
        /// <exception cref="FriendlyMessageException">If the coordinates are no integer values or the list is too short</exception>
        public static Vector3i ParseXZ(List<string> parameters, int fromIndex)
        {
            try
            {
                return new Vector3i(int.Parse(parameters[fromIndex]), 0, int.Parse(parameters[fromIndex + 1]));
            }
            catch (Exception)
            {
                throw new FriendlyMessageException(Resources.ErrorCoordinateNotInteger);
            }
        }

        /// <summary>
        /// Parses three integer coordinates from the given position in the parameter list.
        /// </summary>
        /// <returns>The vector with the three values.</returns>
        /// <exception cref="FriendlyMessageException">If the coordinates are no integer values or the list is too short</exception>
        public static Vector3i ParseXYZ(List<string> parameters, int fromIndex)
        {
            try
            {
                return new Vector3i(int.Parse(parameters[fromIndex]), int.Parse(parameters[fromIndex + 1]), int.Parse(parameters[fromIndex + 2]));
            }
            catch (Exception)
            {
                throw new FriendlyMessageException(Resources.ErrorCoordinateNotInteger);
            }
        }

        /// <summary>
        /// Looks for an option with string value of the format "paramName=lala" in the list of parameters
        /// and if existing returns it. If no such parameter exists, null is returned.
        /// </summary>
        /// <returns>The string of the option value, which can be empty, or null if option does not exist</returns>
        [CanBeNull]
        public static string ParseOption(List<string> parameters, string paramName, bool remove = false)
        {
            var index = parameters.FindIndex(p => p.StartsWith(paramName + "="));
            if (index == -1)
                return null;

            string value = parameters[index].Split(new char[] {'='}, 2)[1];
            if (remove)
                parameters.RemoveAt(index);
            return value;
        }

        /// <summary>
        /// Looks for an option with int value of the format "/paramName=123" in the list of parameters
        /// and if existing parses it to int. If no such parameter exists, null is returned.
        /// </summary>
        /// <returns>The parsed int value, or null if option is missing in parameters</returns>
        /// <exception cref="FriendlyMessageException">If the option exists but cannot be parsed to int</exception>
        [CanBeNull]
        public static int? ParseOptionAsInt(List<string> parameters, string paramName, bool remove = false)
        {
            var value = ParseOption(parameters, paramName, remove);
            if (value == null)
                return null;

            if (!int.TryParse(value, out int result))
                throw new FriendlyMessageException($"The value for parameter {paramName} is not a valid integer.");

            return result;
        }

    }
}
