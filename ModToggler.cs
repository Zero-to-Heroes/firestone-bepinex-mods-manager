using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;
using static FirestoneBepinexModsManager.Plugin;

namespace FirestoneBepinexModsManager
{
    public class ModToggler : MonoBehaviour
    {
        private readonly Dictionary<string, bool> scheduledToggles = new Dictionary<string, bool>();
        private ManualLogSource logger;
        private WebSocketServerPlugin wsServer;

        public void Initialize(ManualLogSource logger, WebSocketServerPlugin wsServer)
        {
            this.logger = logger;
            this.wsServer = wsServer;
            
            // Note: Application.quitting is not available in all Unity versions
            // Cleanup will be handled via OnDestroy() when the GameObject is destroyed
        }

        public void TogglePlugin(string pluginName)
        {
            logger.LogDebug($"TogglePlugin: {pluginName}");
            try
            {
                var fullPath = Path.Combine(Paths.BepInExRootPath, "plugins", pluginName + ".dll");
                
                if (File.Exists(fullPath))
                {
                    logger.LogDebug($"Found plugin: {pluginName}, {fullPath}");
                    // Check if this plugin is currently loaded
                    var isLoaded = Chainloader.PluginInfos.Values.Any(p => 
                        p.Location.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
                    logger.LogDebug($"isLoaded: {isLoaded}");
                    
                    if (isLoaded)
                    {
                        // Cannot rename loaded DLL - schedule for shutdown processing
                        SchedulePluginToggle(pluginName, false);
                        logger.LogWarning($"Plugin {pluginName} is loaded and cannot be disabled until game shutdown");
                        
                        wsServer?.Broadcast($"{{ \"type\": \"plugin-scheduled\", \"plugin\": \"{pluginName}\", \"active\": false, \"message\": \"Plugin scheduled for disable on game shutdown. File is currently locked.\" }}");
                    }
                    else
                    {
                        // Plugin file exists but not loaded - safe to rename
                        var disabledPath = fullPath + ".disabled";
                        File.Move(fullPath, disabledPath);
                        logger.LogInfo($"Disabled plugin: {pluginName}");
                        
                        wsServer?.Broadcast($"{{ \"type\": \"plugin-toggled\", \"plugin\": \"{pluginName}\", \"active\": false, \"message\": \"Plugin disabled successfully.\" }}");
                    }
                }
                else if (File.Exists(fullPath + ".disabled"))
                {
                    // Disabled plugin - enable it (this should always work)
                    File.Move(fullPath + ".disabled", fullPath);
                    logger.LogInfo($"Enabled plugin: {pluginName}");
                    
                    wsServer?.Broadcast($"{{ \"type\": \"plugin-toggled\", \"plugin\": \"{pluginName}\", \"active\": true, \"message\": \"Plugin enabled. Restart game to load it.\" }}");
                }
                else
                {
                    logger.LogError($"Plugin file not found: {pluginName}, {fullPath}");
                    wsServer?.Broadcast($"{{ \"type\": \"error\", \"message\": \"Plugin file not found: {pluginName}\" }}");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogError($"Access denied when toggling plugin {pluginName}: {ex.Message}");
                wsServer?.Broadcast($"{{ \"type\": \"error\", \"message\": \"Cannot modify plugin file - it may be locked by the game. Try restarting the game first.\" }}");
            }
            catch (IOException ex) when (ex.Message.Contains("being used by another process"))
            {
                logger.LogError($"Plugin file is locked: {pluginName}");
                SchedulePluginToggle(pluginName, false);
                wsServer?.Broadcast($"{{ \"type\": \"plugin-scheduled\", \"plugin\": \"{pluginName}\", \"active\": false, \"message\": \"Plugin file is locked. Scheduled for disable on game shutdown.\" }}");
            }
            catch (Exception ex)
            {
                logger.LogError($"Error toggling plugin {pluginName}: {ex.Message}");
                wsServer?.Broadcast($"{{ \"type\": \"error\", \"message\": \"Error toggling plugin: {ex.Message}\" }}");
            }
        }

        private void SchedulePluginToggle(string pluginPath, bool enable)
        {
            scheduledToggles[pluginPath] = enable;
            logger.LogInfo($"Scheduled plugin {pluginPath} to be {(enable ? "enabled" : "disabled")} on game shutdown");
        }

        private void OnDestroy()
        {
            logger?.LogInfo("ModToggler is being destroyed");
            ProcessScheduledTogglesOnShutdown();
        }


        private void ProcessScheduledTogglesOnShutdown()
        {
            if (scheduledToggles.Count == 0) return;
            
            logger?.LogInfo($"Processing {scheduledToggles.Count} scheduled plugin toggles immediately...");
            
            // Process toggles immediately and synchronously to ensure they complete
            // even if the game closes quickly
            ProcessTogglesImmediately();
            
            // Also start the coroutine as backup (in case immediate processing fails)
            try
            {
                StartCoroutine(ProcessTogglesWithDelay());
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Could not start coroutine for delayed processing: {ex.Message}");
            }
        }

        private void ProcessTogglesImmediately()
        {
            var processedToggles = new List<string>();

            foreach (var toggle in scheduledToggles.ToArray())
            {
                try
                {
                    var pluginPath = toggle.Key;
                    var enable = toggle.Value;
                    var fullPath = Path.Combine(Paths.BepInExRootPath, "plugins", pluginPath);

                    bool success = false;
                    if (enable)
                    {
                        // Enable: .disabled -> .dll
                        if (File.Exists(fullPath + ".disabled"))
                        {
                            File.Move(fullPath + ".disabled", fullPath);
                            logger?.LogInfo($"Immediately enabled scheduled plugin during shutdown: {pluginPath}");
                            success = true;
                        }
                    }
                    else
                    {
                        // Disable: .dll -> .disabled
                        if (File.Exists(fullPath))
                        {
                            File.Move(fullPath, fullPath + ".disabled");
                            logger?.LogInfo($"Immediately disabled scheduled plugin during shutdown: {pluginPath}");
                            success = true;
                        }
                    }

                    if (success)
                    {
                        processedToggles.Add(pluginPath);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError($"Failed to immediately process scheduled toggle for {toggle.Key}: {ex.Message}");
                }
            }

            // Remove successfully processed toggles
            foreach (var processed in processedToggles)
            {
                scheduledToggles.Remove(processed);
            }

            if (processedToggles.Count > 0)
            {
                logger?.LogInfo($"Immediately processed {processedToggles.Count} scheduled plugin toggles");
            }
        }

        private IEnumerator ProcessTogglesWithDelay()
        {
            logger?.LogInfo($"Starting delayed processing of {scheduledToggles.Count} scheduled plugin toggles...");
            
            // Wait for other plugins to finish their shutdown procedures
            yield return new WaitForSeconds(1.0f);
            
            var processedToggles = new List<string>();

            foreach (var toggle in scheduledToggles.ToArray())
            {
                try
                {
                    var pluginPath = toggle.Key;
                    var enable = toggle.Value;
                    var fullPath = Path.Combine(Paths.BepInExRootPath, "plugins", pluginPath);

                    // Try multiple times with small delays
                    bool success = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            if (enable)
                            {
                                // Enable: .disabled -> .dll
                                if (File.Exists(fullPath + ".disabled"))
                                {
                                    File.Move(fullPath + ".disabled", fullPath);
                                    logger?.LogInfo($"Enabled scheduled plugin during shutdown: {pluginPath}");
                                    success = true;
                                    break;
                                }
                            }
                            else
                            {
                                // Disable: .dll -> .disabled
                                if (File.Exists(fullPath))
                                {
                                    File.Move(fullPath, fullPath + ".disabled");
                                    logger?.LogInfo($"Disabled scheduled plugin during shutdown: {pluginPath}");
                                    success = true;
                                    break;
                                }
                            }
                        }
                        catch (IOException ex) when (attempt < 2)
                        {
                            logger?.LogWarning($"Attempt {attempt + 1} failed for {pluginPath}, retrying: {ex.Message}");
                            //yield return new WaitForSeconds(0.2f);
                        }
                    }

                    if (success)
                    {
                        processedToggles.Add(pluginPath);
                    }
                    else
                    {
                        logger?.LogError($"Failed to process scheduled toggle for {pluginPath} after 3 attempts");
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError($"Failed to process scheduled toggle for {toggle.Key} during shutdown: {ex.Message}");
                }
            }

            // Remove processed toggles from memory
            foreach (var processed in processedToggles)
            {
                scheduledToggles.Remove(processed);
            }

            if (processedToggles.Count > 0)
            {
                logger?.LogInfo($"Successfully processed {processedToggles.Count} scheduled plugin toggles during shutdown");
            }
            
            if (scheduledToggles.Count > 0)
            {
                logger?.LogWarning($"{scheduledToggles.Count} plugin toggles could not be processed during shutdown");
            }
        }

        public int GetScheduledToggleCount()
        {
            return scheduledToggles.Count;
        }

        public Dictionary<string, bool> GetScheduledToggles()
        {
            return new Dictionary<string, bool>(scheduledToggles);
        }
    }

    public class ToggleModCommand : ModCommand
    {
        public string[] modNames;
    }
}
