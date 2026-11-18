using System;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace CommandItemQueue
{
    /// <summary>
    /// Optional in-game settings page via Risk Of Options (Settings → Mod Options).
    /// Everything goes through reflection, so Risk Of Options is not required and not referenced at compile time.
    /// </summary>
    internal static class RiskOfOptionsCompat
    {
        public const string Guid = "com.rune580.riskofoptions";

        private static Assembly assembly;
        private static MethodInfo addOption;

        public static void TryRegister(PluginConfig cfg, string pluginLocation)
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info?.Instance == null) return;

            try
            {
                assembly = info.Instance.GetType().Assembly;
                var manager = assembly.GetType("RiskOfOptions.ModSettingsManager", true);
                var baseOption = assembly.GetType("RiskOfOptions.Options.BaseOption", true);
                addOption = manager.GetMethod("AddOption", new[] { baseOption, typeof(string), typeof(string) });
                if (addOption == null) throw new MissingMethodException("ModSettingsManager.AddOption(BaseOption, string, string)");

                // General
                Add("CheckBoxOption", cfg.KeepQueueBetweenRuns);
                Add("CheckBoxOption", cfg.OnlyWithArtifactOfCommand);
                Add("CheckBoxOption", cfg.ApplyToVoidPotentials);
                Add("KeyBindOption", cfg.ManualPickKey);
                // UI
                Add("ChoiceOption", cfg.DefaultView);
                Add("KeyBindOption", cfg.SwitchViewKey);
                Add("SliderOption", cfg.UiScale, SliderConfig(0.6f, 1f, "{0:0.00}"));
                Add("CheckBoxOption", cfg.ShowUnavailableItems);
                Add("CheckBoxOption", cfg.ShowTooltips);

                var setDescription = manager.GetMethod("SetModDescription", new[] { typeof(string), typeof(string), typeof(string) });
                setDescription?.Invoke(null, new object[]
                {
                    "Queue items for the Artifact of Command per item tier. Command cubes pick the next queued item automatically. Open the queue with Tab → Item Queue.",
                    CommandItemQueuePlugin.PluginGUID, CommandItemQueuePlugin.PluginName
                });

                var icon = LoadIcon(pluginLocation);
                if (icon)
                {
                    var setIcon = manager.GetMethod("SetModIcon", new[] { typeof(Sprite), typeof(string), typeof(string) });
                    setIcon?.Invoke(null, new object[] { icon, CommandItemQueuePlugin.PluginGUID, CommandItemQueuePlugin.PluginName });
                }

                Log.Info("Risk Of Options found - settings are available in game under Settings → Mod Options.");
            }
            catch (Exception e)
            {
                Log.Warning("Could not register Risk Of Options settings (the mod still works, edit the .cfg file instead): " + e);
            }
        }

        private static void Add(string optionType, ConfigEntryBase entry, object config = null)
        {
            var type = assembly.GetType("RiskOfOptions.Options." + optionType, true);
            object option = config == null
                ? Activator.CreateInstance(type, entry)
                : Activator.CreateInstance(type, entry, config);
            addOption.Invoke(null, new[] { option, CommandItemQueuePlugin.PluginGUID, CommandItemQueuePlugin.PluginName });
        }

        private static object SliderConfig(float min, float max, string format)
        {
            var type = assembly.GetType("RiskOfOptions.OptionConfigs.SliderConfig", true);
            var config = Activator.CreateInstance(type);
            type.GetField("min")?.SetValue(config, min);
            type.GetField("max")?.SetValue(config, max);
            type.GetField("formatString")?.SetValue(config, format);
            return config;
        }

        /// <summary>Icon embedded in the DLL; falls back to the Thunderstore icon.png next to (or above) the DLL.</summary>
        private static Sprite LoadIcon(string pluginLocation)
        {
            try
            {
                using (var stream = typeof(RiskOfOptionsCompat).Assembly.GetManifestResourceStream("CommandItemQueue.icon.png"))
                {
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        var embedded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (ImageConversion.LoadImage(embedded, bytes))
                            return Sprite.Create(embedded, new Rect(0f, 0f, embedded.width, embedded.height), new Vector2(0.5f, 0.5f));
                    }
                }

                var dir = Path.GetDirectoryName(pluginLocation);
                for (int i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
                {
                    var path = Path.Combine(dir, "icon.png");
                    if (File.Exists(path))
                    {
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) return null;
                        return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception e)
            {
                Log.Warning("Could not load icon for Risk Of Options: " + e.Message);
            }
            return null;
        }
    }
}
