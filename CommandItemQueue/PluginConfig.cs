using BepInEx.Configuration;
using UnityEngine;

namespace CommandItemQueue
{
    public enum StartView
    {
        RememberLast,
        Scoreboard,
        ItemQueue
    }

    internal sealed class PluginConfig
    {
        public readonly ConfigEntry<bool> KeepQueueBetweenRuns;
        public readonly ConfigEntry<bool> OnlyWithArtifactOfCommand;
        public readonly ConfigEntry<bool> ApplyToVoidPotentials;
        public readonly ConfigEntry<KeyboardShortcut> ManualPickKey;

        public readonly ConfigEntry<StartView> DefaultView;
        public readonly ConfigEntry<KeyboardShortcut> SwitchViewKey;
        public readonly ConfigEntry<float> UiScale;
        public readonly ConfigEntry<bool> ShowUnavailableItems;
        public readonly ConfigEntry<bool> ShowTooltips;

        public readonly ConfigEntry<bool> LastViewWasQueue;
        public readonly ConfigEntry<string> DiscoveredCategories;

        public PluginConfig(ConfigFile cfg)
        {
            KeepQueueBetweenRuns = cfg.Bind("General", "KeepQueueBetweenRuns", false,
                "Keep the current queue when a new run starts. If false, the queue is cleared (an auto-load build is still applied).");
            OnlyWithArtifactOfCommand = cfg.Bind("General", "OnlyWithArtifactOfCommand", true,
                "Only show the Item Queue tab in the scoreboard when the Artifact of Command is enabled.");
            ApplyToVoidPotentials = cfg.Bind("General", "ApplyToVoidPotentials", false,
                "Also auto-pick from Void Potentials / option pickups if one of the offered items is in your queue.");
            ManualPickKey = cfg.Bind("General", "ManualPickKey", new KeyboardShortcut(KeyCode.LeftAlt),
                "Hold this key while opening a command cube to ignore the queue and choose manually.");

            DefaultView = cfg.Bind("UI", "DefaultView", StartView.RememberLast,
                "Which view is shown when you open the scoreboard (Tab).");
            SwitchViewKey = cfg.Bind("UI", "SwitchViewKey", KeyboardShortcut.Empty,
                "Optional key to switch between Scoreboard and Item Queue while the scoreboard is open (you can always click the tabs).");
            UiScale = cfg.Bind("UI", "Scale", 1f,
                new ConfigDescription("Size of the Item Queue window. 1 = as large as fits the screen, smaller values shrink it.", new AcceptableValueRange<float>(0.6f, 1f)));
            ShowUnavailableItems = cfg.Bind("UI", "ShowUnavailableItems", false,
                "Also show items that cannot drop in the current run (locked, disabled expansion, ...). They are skipped when picking.");
            ShowTooltips = cfg.Bind("UI", "ShowTooltips", true,
                "Show item name and description when hovering an icon.");

            LastViewWasQueue = cfg.Bind("Internal", "LastViewWasQueue", false,
                "Remembers the last selected view. No need to edit.");
            DiscoveredCategories = cfg.Bind("Internal", "DiscoveredCategories", "",
                "Extra command categories that were discovered at runtime (non-item, non-equipment pickups). No need to edit.");
        }
    }
}
