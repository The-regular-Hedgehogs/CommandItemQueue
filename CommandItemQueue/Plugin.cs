using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using RoR2;

namespace CommandItemQueue
{
    // Client-side only: the choice is sent through the vanilla command-cube network message,
    // so this works even if the host or other players don't have the mod installed.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class CommandItemQueuePlugin : BaseUnityPlugin
    {
        public const string PluginAuthor = "AllOfThem";
        public const string PluginName = "CommandItemQueue";
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginVersion = "1.0.1";

        internal static CommandItemQueuePlugin Instance { get; private set; }
        internal static PluginConfig Cfg { get; private set; }

        internal QueueManager Queues { get; private set; }
        internal BuildStore Builds { get; private set; }
        internal ScoreboardBridge Scoreboard { get; private set; }
        internal QueueWindow Window { get; private set; }

        private Harmony harmony;

        public void Awake()
        {
            Instance = this;
            Log.Init(Logger);

            Cfg = new PluginConfig(Config);
            PickupGroups.LoadDiscovered(Cfg.DiscoveredCategories.Value);

            Queues = new QueueManager();
            Builds = new BuildStore(System.IO.Path.Combine(Paths.ConfigPath, PluginName + ".builds.txt"));
            Builds.Load();

            Scoreboard = new ScoreboardBridge();
            Window = new QueueWindow(this);

            harmony = new Harmony(PluginGUID);
            AutoPicker.Install(harmony);

            Run.onRunStartGlobal += OnRunStart;
            Run.onRunDestroyGlobal += OnRunDestroy;
        }

        public void OnDestroy()
        {
            Run.onRunStartGlobal -= OnRunStart;
            Run.onRunDestroyGlobal -= OnRunDestroy;
            try { harmony?.UnpatchSelf(); } catch (Exception e) { Log.Warning(e); }
            Scoreboard?.SetHidden(false);
        }

        private void OnRunStart(Run run)
        {
            try
            {
                PickupGroups.Invalidate();
                if (!Cfg.KeepQueueBetweenRuns.Value)
                    Queues.ClearAll();
                Queues.ResetRepeat(); // every run starts with all categories on "run once"
                Queues.ResetCursors();

                var auto = Builds.GetAutoLoadBuild();
                if (auto != null)
                {
                    Queues.Apply(auto);
                    Log.Info($"Auto-loaded build '{auto.Name}'.");
                }
            }
            catch (Exception e) { Log.Error(e); }
        }

        private void OnRunDestroy(Run run)
        {
            PickupGroups.Invalidate();
            Scoreboard?.SetHidden(false);
        }

        public void Update()
        {
            try { AutoPicker.Update(); } catch (Exception e) { Log.Error(e); }
            try { Window.Update(); } catch (Exception e) { Log.Error(e); }
        }

        public void OnGUI()
        {
            Window.OnGUI();
        }

        internal void SaveDiscoveredCategories()
        {
            Cfg.DiscoveredCategories.Value = PickupGroups.SerializeDiscovered();
        }
    }
}
