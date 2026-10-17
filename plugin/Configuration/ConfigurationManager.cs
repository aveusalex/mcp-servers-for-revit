using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace revit_mcp_plugin.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly string _configPath;

        public FrameworkConfig Config { get; private set; }

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;

            // 配置文件路径
            // Configuration file path.
            _configPath = PathManager.GetCommandRegistryFilePath();
        }

        /// <summary>
        /// <para>加载配置</para>
        /// <para>Load configuration from a JSON file.</para>
        /// </summary>
        public void LoadConfiguration()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    Config = JsonConvert.DeserializeObject<FrameworkConfig>(json);
                    _logger.Info("已加载配置文件: {0}\nConfiguration file loaded: {0}", _configPath);
                }
                else
                {
                    _logger.Error("未找到配置文件\nNo configuration file found.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("加载配置文件失败: {0}\nFailed to load configuration file: {0}", ex.Message);
            }

            SeedFromCommandSets();

            // 记录加载时间
            // Register load time.
            _lastConfigLoadTime = DateTime.Now;
        }

        ///// <summary>
        ///// <para>重新加载配置</para>
        ///  <para>Reload configuration.</para>
        ///// </summary>
        //public void RefreshConfiguration()
        //{
        //    LoadConfiguration();
        //    _logger.Info("配置已重新加载\nConfiguration has been reloaded.");
        //}

        //public bool HasConfigChanged()
        //{
        //    if (!File.Exists(_configPath))
        //        return false;

        //    DateTime lastWrite = File.GetLastWriteTime(_configPath);
        //    return lastWrite > _lastConfigLoadTime;
        //}

        /// <summary>
        /// Adds, enabled, every command that an installed command set declares in its command.json but the
        /// registry doesn't list yet. A fresh install otherwise starts with an empty registry and loads no legacy
        /// command until someone enables each one in Settings. Commands already in the registry keep their
        /// state, so one a user disabled stays disabled.
        /// </summary>
        private void SeedFromCommandSets()
        {
            if (Config == null)
            {
                Config = new FrameworkConfig();
            }

            var known = new HashSet<string>(Config.Commands.Select(c => c.CommandName), StringComparer.OrdinalIgnoreCase);
            var added = new List<string>();

            try
            {
                string commandsDir = PathManager.GetCommandsDirectoryPath();
                foreach (string setDir in Directory.GetDirectories(commandsDir))
                {
                    string manifestPath = Path.Combine(setDir, "command.json");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                    string setName = (string)manifest["name"] ?? Path.GetFileName(setDir);
                    foreach (JToken command in manifest["commands"] ?? new JArray())
                    {
                        string name = (string)command["commandName"];
                        string assembly = (string)command["assemblyPath"];
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(assembly) || known.Contains(name))
                        {
                            continue;
                        }

                        Config.Commands.Add(new CommandConfig
                        {
                            CommandName = name,
                            AssemblyPath = Path.Combine(setName, "{VERSION}", assembly),
                            Enabled = true,
                            Description = (string)command["description"] ?? "",
                            Scope = (string)command["scope"] ?? "doc-agnostic",
                        });
                        known.Add(name);
                        added.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to seed the command registry from command sets: {0}", ex.Message);
                return;
            }

            if (added.Count == 0)
            {
                return;
            }

            try
            {
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
                _logger.Info("Added {0} command(s) from installed command sets to the registry: {1}", added.Count, string.Join(", ", added));
            }
            catch (Exception ex)
            {
                // Still usable for this session; it is retried on the next start.
                _logger.Warning("Seeded {0} command(s) but could not save the registry: {1}", added.Count, ex.Message);
            }
        }

        private DateTime _lastConfigLoadTime;
    }
}
