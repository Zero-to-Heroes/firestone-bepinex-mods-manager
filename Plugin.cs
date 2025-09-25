using BepInEx;
using BepInEx.Logging;
using BepInEx.Bootstrap;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace FirestoneBepinexModsManager
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private WebSocketServerPlugin wsServer;
        private ModToggler modToggler;
        private PluginDiscovery pluginDiscovery;

        private void Awake()
        {
            try
            {

                // Plugin startup logic
                Logger = base.Logger;
                Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

                // Initialize plugin discovery
                this.pluginDiscovery = new PluginDiscovery(Logger);
                Logger.LogInfo("PluginDiscovery initialized");

                Logger.LogDebug($"Creating websocket");
                this.wsServer = new WebSocketServerPlugin();
                int port = 9977;
                var wsLocation = this.wsServer.OpenServer(port, new Action(PublishModsInfo), new Action<string>(OnMessage));
                Logger.LogInfo($"Websocket server created, listening on port {port} at location {wsLocation}");

                // Initialize mod toggler
                var togglerGO = new GameObject("ModToggler");
                DontDestroyOnLoad(togglerGO);
                this.modToggler = togglerGO.AddComponent<ModToggler>();
                this.modToggler.Initialize(Logger, this.wsServer);
                Logger.LogInfo("ModToggler initialized");
            }
            catch (Exception e)
            {
                Logger.LogError($"Could not initialize Mods Manager {e.ToString()}");
            }
        }

        private void PublishModsInfo()
        {
            Logger.LogInfo($"Ready to build plugins info {this.pluginDiscovery}");
            var allPlugins = this.pluginDiscovery.GetAllPlugins();
            Logger.LogInfo($"Building plugins info: {allPlugins.Count} plugins found");
            var stringified = JsonConvert.SerializeObject(allPlugins);
            Logger.LogInfo($"Publishing mods info: {allPlugins.Count} plugins found");
            this.wsServer?.Broadcast($"{{ \"type\": \"mods-info\", \"data\": {stringified} }}");
        }

        private void PublishScheduledToggles()
        {
            var scheduledToggles = this.modToggler?.GetScheduledToggles() ?? new Dictionary<string, bool>();
            var stringified = JsonConvert.SerializeObject(scheduledToggles);
            Logger.LogInfo($"Publishing scheduled toggles info: {scheduledToggles.Count} scheduled");
            this.wsServer?.Broadcast($"{{ \"type\": \"scheduled-toggles\", \"data\": {stringified} }}");
        }


        private void OnMessage(string message)
        {
            try
            {
                Logger.LogInfo($"Received WebSocket message: {message}");
                var command = JsonConvert.DeserializeObject<ModCommand>(message);

                switch (command.Type.ToLower())
                {
                    case "toggle-mod":
                        this.modToggler?.TogglePlugin(command.PluginPath);
                        break;
                    case "get-plugins":
                        PublishModsInfo();
                        break;
                    case "get-scheduled-toggles":
                        PublishScheduledToggles();
                        break;
                    default:
                        Logger.LogWarning($"Unknown command type: {command.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error processing message: {ex.Message}");
            }
        }


        public class ModCommand
        {
            public string Type { get; set; }
            public string PluginPath { get; set; }
        }
    }
}
