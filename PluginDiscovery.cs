using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FirestoneBepinexModsManager
{
    public class PluginDiscovery
    {
        private readonly ManualLogSource logger;

        public PluginDiscovery(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public List<PluginInfo> GetAllPlugins()
        {
            var plugins = new List<PluginInfo>();
            var pluginsPath = Path.Combine(Paths.BepInExRootPath, "plugins");
            
            if (!Directory.Exists(pluginsPath))
            {
                logger.LogWarning($"Plugins directory not found: {pluginsPath}");
                return plugins;
            }

            // Get all .dll files (active plugins)
            var activeDlls = Directory.GetFiles(pluginsPath, "*.dll", SearchOption.AllDirectories);
            foreach (var dllPath in activeDlls)
            {
                var pluginInfo = CreatePluginInfo(dllPath, true);
                if (pluginInfo != null) plugins.Add(pluginInfo);
            }

            // Get all .dll.disabled files (inactive plugins)
            var disabledDlls = Directory.GetFiles(pluginsPath, "*.dll.disabled", SearchOption.AllDirectories);
            foreach (var dllPath in disabledDlls)
            {
                var pluginInfo = CreatePluginInfo(dllPath, false);
                if (pluginInfo != null) plugins.Add(pluginInfo);
            }

            return plugins;
        }

        private PluginInfo CreatePluginInfo(string filePath, bool isActive)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                bool isLoaded = false;
                
                // Check if plugin is loaded in BepInEx
                if (isActive)
                {
                    isLoaded = Chainloader.PluginInfos.Values.Any(p => 
                        p.Location.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                }

                // Load assembly to extract metadata
                var pluginInfo = LoadAssemblyMetadata(filePath, isActive, isLoaded);
                return pluginInfo;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error reading plugin info from {filePath}: {ex.Message}");
                return null;
            }
        }

        private PluginInfo LoadAssemblyMetadata(string filePath, bool isActive, bool isLoaded)
        {
            Assembly assembly = null;
            try
            {
                // For disabled plugins, we need to temporarily rename them to load
                string actualPath = filePath;
                bool wasRenamed = false;
                
                if (filePath.EndsWith(".disabled"))
                {
                    actualPath = filePath.Replace(".disabled", "");
                    if (!File.Exists(actualPath))
                    {
                        File.Copy(filePath, actualPath);
                        wasRenamed = true;
                    }
                }

                // Load assembly for reflection only to avoid conflicts
                try
                {
                    assembly = Assembly.ReflectionOnlyLoadFrom(actualPath);
                }
                catch
                {
                    // Fallback to regular load if reflection-only fails
                    assembly = Assembly.LoadFrom(actualPath);
                }

                // Clean up temporary file
                if (wasRenamed && File.Exists(actualPath))
                {
                    File.Delete(actualPath);
                }

                return ExtractPluginInfoFromAssembly(assembly, isActive, isLoaded);
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to load assembly metadata from {filePath}: {ex.Message}");
                
                // Fallback to basic info
                var fileName = Path.GetFileName(filePath);
                return new PluginInfo
                {
                    Name = Path.GetFileNameWithoutExtension(fileName.Replace(".disabled", "")),
                    Active = isActive,
                    Loaded = isLoaded,
                    Version = "Unknown",
                    DownloadLink = null,
                    AssemblyName = Path.GetFileNameWithoutExtension(fileName.Replace(".disabled", ""))
                };
            }
            finally
            {
                // Assembly will be garbage collected
            }
        }

        private PluginInfo ExtractPluginInfoFromAssembly(Assembly assembly, bool isActive, bool isLoaded)
        {
            string name = "Unknown";
            string version = "Unknown";
            string downloadLink = null;
            string assemblyName = assembly.GetName().Name;

            try
            {
                // Look for BepInPlugin attribute
                var bepInPluginAttr = assembly.GetCustomAttributes(typeof(BepInPlugin), false)
                    .Cast<BepInPlugin>()
                    .FirstOrDefault();

                if (bepInPluginAttr != null)
                {
                    name = bepInPluginAttr.Name;
                    version = bepInPluginAttr.Version?.ToString();
                }

                // Try to get version from AssemblyVersion if not found in BepInPlugin
                if (version == "Unknown")
                {
                    version = assembly.GetName().Version?.ToString() ?? "Unknown";
                }

                // Look for custom attributes that might contain download links
                // Common attributes: AssemblyMetadata, AssemblyDescription, etc.
                var metadataAttrs = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                    .Cast<System.Reflection.AssemblyMetadataAttribute>();

                foreach (var attr in metadataAttrs)
                {
                    if (attr.Key.ToLower().Contains("url") || attr.Key.ToLower().Contains("link") || 
                        attr.Key.ToLower().Contains("homepage") || attr.Key.ToLower().Contains("repository"))
                    {
                        downloadLink = attr.Value;
                        break;
                    }
                }

                // Also check AssemblyDescription for URLs
                if (string.IsNullOrEmpty(downloadLink))
                {
                    var descAttr = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyDescriptionAttribute), false)
                        .Cast<System.Reflection.AssemblyDescriptionAttribute>()
                        .FirstOrDefault();

                    if (descAttr != null && (descAttr.Description.Contains("http://") || descAttr.Description.Contains("https://")))
                    {
                        // Extract URL from description
                        var urlMatch = System.Text.RegularExpressions.Regex.Match(descAttr.Description, @"https?://[^\s]+");
                        if (urlMatch.Success)
                        {
                            downloadLink = urlMatch.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error extracting metadata from assembly {assembly.GetName().Name}: {ex.Message}");
            }

            return new PluginInfo
            {
                Name = name,
                Active = isActive,
                Loaded = isLoaded,
                Version = version,
                DownloadLink = downloadLink,
                AssemblyName = assemblyName
            };
        }
    }

    public class PluginInfo
    {
        public string Name { get; set; }
        public bool Active { get; set; }
        public bool Loaded { get; set; }
        public string Version { get; set; }
        public string DownloadLink { get; set; }
        public string AssemblyName { get; set; }
    }
}
