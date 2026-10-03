using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SnowFlyer2
{
    using Newtonsoft.Json;
    using System;
    using System.IO;
    using System.Windows.Forms;

    public class KeybindingManager
    {
        private const string ConfigFileName = "keybinds.json";
        private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        public KeybindingConfig ReadKeybindingConfig()
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                KeybindingConfig config = JsonConvert.DeserializeObject<KeybindingConfig>(json);

                // Keep existing keybind files compatible with the tracking prototype.
                if (config.TogglePlayerTracking == Keys.None)
                    config.TogglePlayerTracking = Keys.F5;

                if (config.CaptureCameraScan == Keys.None)
                    config.CaptureCameraScan = Keys.F6;

                if (config.FindCameraPointers == Keys.None)
                    config.FindCameraPointers = Keys.F7;

                if (config.ResetCameraScan == Keys.None)
                    config.ResetCameraScan = Keys.F8;

                if (config.ProbeCameraChain == Keys.None)
                    config.ProbeCameraChain = Keys.F9;

                if (config.ToggleCameraTracking == Keys.None)
                    config.ToggleCameraTracking = Keys.F10;

                if (config.ProbeCameraTransform == Keys.None)
                    config.ProbeCameraTransform = Keys.F11;

                if (config.ToggleCameraFollow == Keys.None)
                    config.ToggleCameraFollow = Keys.F12;

                return config;
            }
            else
            {
                // Default keybinding if the file doesn't exist
                KeybindingConfig defaultBinds = new KeybindingConfig { Instructions= "The keybinds are hex key modifiers - see microsoft docs for a full list. https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes", ToggleFlyMode = Keys.F1, ToggleFreezeTime=Keys.F2, ToggleTimeLapseMode=Keys.F3, GetPlayerLocation=Keys.F4, TogglePlayerTracking=Keys.F5, CaptureCameraScan=Keys.F6, FindCameraPointers=Keys.F7, ResetCameraScan=Keys.F8, ProbeCameraChain=Keys.F9, ToggleCameraTracking=Keys.F10, ProbeCameraTransform=Keys.F11, ToggleCameraFollow=Keys.F12};
                SaveKeybindingConfig(defaultBinds);
                return defaultBinds;
            }
        }

        public void SaveKeybindingConfig(KeybindingConfig config)
        {
            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(ConfigFilePath, json);
        }
    }

}
