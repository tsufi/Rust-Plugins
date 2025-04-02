using Oxide.Core;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("AutoMessages", "Tsufi", "1.0.0")]
    [Description("Sends automated chat messages at regular intervals.")]

    public class AutoMessages : RustPlugin
    {
        private Timer messageTimer;
        private int messageIndex = 0;

        private ConfigData config;

        private class ConfigData
        {
            public int IntervalSeconds = 300;
            public bool RandomOrder = false;
            public string Prefix = "<color=#ffcc00>[Server]</color>";
            public List<string> Messages = new List<string>
            {
                "Welcome to the server!",
                "Join our Discord: discord.gg/example",
                "Don't forget to vote for our server!",
                "Use /help for useful commands."
            };
        }

        protected override void LoadDefaultConfig() => config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<ConfigData>() ?? new ConfigData();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        private void OnServerInitialized()
        {
            if (config.Messages == null || config.Messages.Count == 0)
            {
                PrintWarning("No messages configured. AutoMessages will not run.");
                return;
            }

            StartBroadcasting();
        }

        private void StartBroadcasting()
        {
            messageTimer = timer.Every(config.IntervalSeconds, () =>
            {
                string message;

                if (config.RandomOrder)
                {
                    message = config.Messages.GetRandom();
                }
                else
                {
                    message = config.Messages[messageIndex % config.Messages.Count];
                    messageIndex++;
                }

                Server.Broadcast($"{config.Prefix} {message}");
            });
        }

        private void Unload()
        {
            messageTimer?.Destroy();
        }
    }
}
