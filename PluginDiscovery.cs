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
                logger.LogDebug($"Considering {dllPath}");
                var pluginInfo = CreatePluginInfo(dllPath, true);
                logger.LogDebug($"\tpluginInfo: {pluginInfo}");
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
                
                // First, do a lightweight check to see if this DLL likely contains BepInEx plugins
                if (!IsLikelyBepInExPlugin(filePath))
                {
                    logger.LogDebug($"Not a BepInEx plugin: {filePath}");
                    return null; // Skip this file entirely
                }
                
                // Check if plugin is loaded in BepInEx
                if (isActive)
                {
                    isLoaded = Chainloader.PluginInfos.Values.Any(p => 
                        p.Location.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                }

                // Load assembly to extract metadata
                var pluginInfo = LoadAssemblyMetadata(filePath, isActive, isLoaded);
                // Only return plugin info if it's a valid BepInEx plugin
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

                // Try different loading strategies in order of preference
                assembly = LoadAssemblyWithFallback(actualPath);

                // Clean up temporary file
                if (wasRenamed && File.Exists(actualPath))
                {
                    File.Delete(actualPath);
                }

                var pluginInfo = ExtractPluginInfoFromAssembly(assembly, isActive, isLoaded);
                return pluginInfo; // Will be null if no valid BepInEx plugin found
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
                // First, check if this assembly contains any classes that extend BaseUnityPlugin
                bool hasValidBepInExPlugin = false;
                BepInPlugin bepInPluginAttr = null;

                foreach (var type in assembly.GetTypes())
                {
                    if (IsBaseUnityPluginSubclass(type))
                    {
                        hasValidBepInExPlugin = true;
                        
                        // Look for BepInPlugin attribute on this type
                        var attr = type.GetCustomAttributes(typeof(BepInPlugin), false)
                            .Cast<BepInPlugin>()
                            .FirstOrDefault();
                        
                        if (attr != null)
                        {
                            bepInPluginAttr = attr;
                            break; // Use the first valid plugin found
                        }
                    }
                }

                // If no valid BepInEx plugin found, return null to exclude this assembly
                if (!hasValidBepInExPlugin)
                {
                    logger.LogDebug($"Not a valid bepInEx plugin: {assemblyName}");
                    return null;
                }

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

        private bool IsLikelyBepInExPlugin(string filePath)
        {
            try
            {
                // If it's a disabled plugin, we can safely assume it's a BepInEx plugin
                // (it was disabled by BepInEx or a plugin manager, so it was recognized as a plugin)
                if (filePath.EndsWith(".disabled"))
                {
                    return true;
                }
                
                // For active plugins, do the lightweight check
                string actualPath = filePath;

                // Use AssemblyName.GetAssemblyName for lightweight metadata reading
                var assemblyName = AssemblyName.GetAssemblyName(actualPath);
                
                // Quick check: if it's a system assembly, skip it
                if (IsSystemAssembly(assemblyName.Name))
                {
                    return false;
                }

                logger.LogDebug($"\t Not a system assemby {filePath}");
                return true;
                //// Check if the assembly references BepInEx by examining the file's metadata
                //// This is much lighter than loading the full assembly
                //using (var fileStream = new FileStream(actualPath, FileMode.Open, FileAccess.Read))
                //{
                //    // Read PE header to find .NET metadata
                //    var buffer = new byte[1024];
                //    fileStream.Read(buffer, 0, buffer.Length);
                    
                //    // Convert to string and check for BepInEx references
                //    var content = System.Text.Encoding.ASCII.GetString(buffer);
                //    logger.LogDebug($"PE header: {content}");
                //    if (content.Contains("BepInEx") || content.Contains("BaseUnityPlugin"))
                //    {
                //        return true;
                //    }
                //}

                //return false;
            }
            catch (Exception ex)
            {
                logger.LogDebug($"Error in lightweight BepInEx check for {filePath}: {ex.Message}");
                // If we can't do the lightweight check, assume it might be a plugin
                return true;
            }
        }

        private Assembly LoadAssemblyWithFallback(string assemblyPath)
        {
            // Strategy 1: Try to get assembly info from already loaded assemblies first
            // This avoids loading issues for plugins that are already loaded by BepInEx
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name.Equals(assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            
            if (loadedAssembly != null)
            {
                logger.LogDebug($"Using already loaded assembly: {assemblyName.Name}");
                return loadedAssembly;
            }

            // Strategy 2: Try LoadFrom (safer than ReflectionOnly for BepInEx plugins)
            try
            {
                logger.LogDebug($"Loading assembly from file: {assemblyPath}");
                return Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug($"LoadFrom failed for {assemblyPath}: {ex.Message}");
            }

            // Strategy 3: Try ReflectionOnlyLoadFrom as last resort
            try
            {
                logger.LogDebug($"Trying reflection-only load for: {assemblyPath}");
                return Assembly.ReflectionOnlyLoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug($"ReflectionOnlyLoadFrom failed for {assemblyPath}: {ex.Message}");
                throw; // Re-throw the last exception
            }
        }

        private bool IsSystemAssembly(string assemblyName)
        {
            var systemAssemblies = new[]
            {
                "mscorlib", "System", "System.Core", "System.Data", "System.Drawing",
                "System.Windows.Forms", "System.Xml", "netstandard", "System.Runtime",
                "Unity", "UnityEngine", "UnityEditor", "Mono.", "Microsoft.",
                "Newtonsoft.Json", "Google.Protobuf", "Polly", "Fleck"
            };

            return systemAssemblies.Any(sysAsm => assemblyName.Contains(sysAsm));
        }

        private bool IsBaseUnityPluginSubclass(Type type)
        {
            try
            {
                // Check if the type is a class and not abstract
                if (!type.IsClass || type.IsAbstract)
                    return false;

                // Walk up the inheritance chain to find BaseUnityPlugin
                Type currentType = type;
                while (currentType != null && currentType != typeof(object))
                {
                    if (currentType.Name == "BaseUnityPlugin" && 
                        currentType.Namespace == "BepInEx")
                    {
                        return true;
                    }
                    currentType = currentType.BaseType;
                }

                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Error checking if type {type.Name} extends BaseUnityPlugin: {ex.Message}");
                return false;
            }
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
