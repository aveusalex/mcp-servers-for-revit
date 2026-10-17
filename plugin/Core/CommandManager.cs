using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Utils;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>命令管理器，负责加载和管理命令</para>
    /// <para>Command Manager</para>
    /// </summary>
    public class CommandManager
    {
        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;
        private readonly ConfigurationManager _configManager;
        private readonly UIApplication _uiApplication;
        private readonly RevitVersionAdapter _versionAdapter;

        /// <summary>
        /// Manager in charge of loading and managing commands.
        /// </summary>
        /// <param name="commandRegistry"></param>
        /// <param name="logger"></param>
        /// <param name="configManager"></param>
        /// <param name="uiApplication"></param>
        public CommandManager(
            ICommandRegistry commandRegistry,
            ILogger logger,
            ConfigurationManager configManager,
            UIApplication uiApplication)
        {
            _commandRegistry = commandRegistry;
            _logger = logger;
            _configManager = configManager;
            _uiApplication = uiApplication;
            _versionAdapter = new RevitVersionAdapter(_uiApplication.Application);
        }

        /// <summary>
        /// <para>加载配置文件中指定的所有命令.</para>
        /// <para>Load all commands specified in the configuration file.</para>
        /// </summary>
        public void LoadCommands()
        {
            _logger.Info("开始加载命令\nStart loading command.");
            string currentVersion = _versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);

            // 从配置加载外部命令
            // Load external commands from the configuration file.
            foreach (var commandConfig in _configManager.Config.Commands)
            {
                try
                {
                    if (!commandConfig.Enabled)
                    {
                        _logger.Info("跳过禁用的命令: {0}\nSkipping disabled command: {0}", commandConfig.CommandName);
                        continue;
                    }

                    // 检查版本兼容性
                    // Check Revit version compatibility.
                    if (commandConfig.SupportedRevitVersions != null &&
                        commandConfig.SupportedRevitVersions.Length > 0 &&
                        !_versionAdapter.IsVersionSupported(commandConfig.SupportedRevitVersions))
                    {
                        _logger.Warning("命令 {0} 不支持当前 Revit 版本 {1}，已跳过\nThe command {0} is not supported by the current Revit version ({1}} and it has been skipped.",
                            commandConfig.CommandName, currentVersion);
                        continue;
                    }

                    // 替换路径中的版本占位符
                    // Replace version placeholder strings in paths.
                    commandConfig.AssemblyPath = commandConfig.AssemblyPath.Contains("{VERSION}")
                        ? commandConfig.AssemblyPath.Replace("{VERSION}", currentVersion)
                        : commandConfig.AssemblyPath;

                    // 加载外部命令程序集
                    // Load external command assembly.
                    LoadCommandFromAssembly(commandConfig);
                }
                catch (Exception ex)
                {
                    _logger.Error("加载命令 {0} 失败: {1}\nFailed to load command {0}: {1}", commandConfig.CommandName, ex.Message);
                }
            }

            // Framework tools ([McpTool] classes) register themselves: no command.json entry or enabling needed.
            LoadFrameworkTools(currentVersion);

            _logger.Info("命令加载完成\nCommand loading complete.");
        }

        // --- framework tools (Phase 0) ---------------------------------------------

        private const string ToolRegistryTypeName = "RevitMCPCommandSet.Framework.ToolRegistry";
        private static readonly object ManifestLock = new object();
        private static JToken _toolsManifest;

        /// <summary>
        /// Registers the framework tools of every command-set assembly that has a ToolRegistry. Uses reflection
        /// only: the plugin shares no compile-time type with the command set. Tool names that a legacy command
        /// already registered are left to the framework tool (it is the newer implementation).
        /// </summary>
        private void LoadFrameworkTools(string revitVersion)
        {
            foreach (var assembly in LoadCommandSetAssemblies(revitVersion, _configManager, _logger))
            {
                try
                {
                    var registryType = assembly.GetType(ToolRegistryTypeName, false);
                    var create = registryType?.GetMethod("CreateCommands", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(UIApplication) }, null);
                    if (create == null) continue;

                    int count = 0;
                    if (create.Invoke(null, new object[] { _uiApplication }) is IEnumerable commands)
                    {
                        foreach (var item in commands)
                        {
                            if (item is IRevitCommand command)
                            {
                                _commandRegistry.RegisterCommand(command);
                                count++;
                            }
                        }
                    }
                    _logger.Info("Registered {0} framework tool(s) from {1}", count, assembly.GetName().Name);

                    var manifest = ReadManifest(registryType);
                    if (manifest != null)
                    {
                        lock (ManifestLock) { _toolsManifest = manifest; }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to register framework tools from {0}: {1}",
                        assembly.GetName().Name, (ex.InnerException ?? ex).Message);
                }
            }
        }

        /// <summary>
        /// The tools manifest (Contract D) sent to the broker in <c>register</c>, or null when no command set
        /// with framework tools is installed. Safe from any thread; loads the command-set assembly on first use
        /// so the manifest is available even before the command registry is loaded on Revit's thread.
        /// </summary>
        public static JToken GetToolsManifest(string revitVersion)
        {
            lock (ManifestLock)
            {
                if (_toolsManifest != null) return _toolsManifest;
            }

            if (string.IsNullOrEmpty(revitVersion) || revitVersion == "unknown") return null;

            try
            {
                foreach (var assembly in LoadCommandSetAssemblies(revitVersion, null, null))
                {
                    var manifest = ReadManifest(assembly.GetType(ToolRegistryTypeName, false));
                    if (manifest == null) continue;
                    lock (ManifestLock) { _toolsManifest = _toolsManifest ?? manifest; return _toolsManifest; }
                }
            }
            catch
            {
                // No manifest is better than a failed registration.
            }
            return null;
        }

        private static JToken ReadManifest(Type registryType)
        {
            var method = registryType?.GetMethod("GetManifestJson", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            return method?.Invoke(null, null) is string json ? JToken.Parse(json) : null;
        }

        /// <summary>
        /// Command-set assemblies to scan: the ones command.json points to, plus the conventional
        /// <c>Commands\&lt;Set&gt;\&lt;version&gt;\&lt;Set&gt;.dll</c> layout, so a set ships framework tools without any
        /// command.json entry.
        /// </summary>
        private static List<Assembly> LoadCommandSetAssemblies(string revitVersion, ConfigurationManager config, ILogger logger)
        {
            var paths = new List<string>();
            string baseDir = PathManager.GetCommandsDirectoryPath();

            if (config?.Config?.Commands != null)
            {
                foreach (var command in config.Config.Commands)
                {
                    if (string.IsNullOrEmpty(command.AssemblyPath)) continue;
                    string path = command.AssemblyPath.Replace("{VERSION}", revitVersion);
                    if (!Path.IsPathRooted(path)) path = Path.Combine(baseDir, path);
                    paths.Add(path);
                }
            }

            if (Directory.Exists(baseDir))
            {
                foreach (var setDir in Directory.GetDirectories(baseDir))
                {
                    string setName = Path.GetFileName(setDir);
                    paths.Add(Path.Combine(setDir, revitVersion, setName + ".dll"));
                }
            }

            var result = new List<Assembly>();
            foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var assembly = Assembly.LoadFrom(path);
                    if (!result.Contains(assembly)) result.Add(assembly);
                }
                catch (Exception ex)
                {
                    logger?.Warning("Could not load command set {0}: {1}", path, ex.Message);
                }
            }
            return result;
        }

        /// <summary>
        /// 加载特定程序集中的特定命令
        /// Loads specific commands in specific assemblies.
        /// </summary>
        /// <param name="config">Configuration class describing the command.</param>
        private void LoadCommandFromAssembly(CommandConfig config)
        {
            try
            {
                // 确定程序集路径
                // Determine the assembly path.
                string assemblyPath = config.AssemblyPath;
                if (!Path.IsPathRooted(assemblyPath))
                {
                    // 如果不是绝对路径，则相对于Commands目录
                    // If it is not an absolute path, then it is relative to the Command's directory.
                    string baseDir = PathManager.GetCommandsDirectoryPath();
                    assemblyPath = Path.Combine(baseDir, assemblyPath);
                }

                if (!File.Exists(assemblyPath))
                {
                    _logger.Error("命令程序集不存在: {0}\nCommand assembly does not exist: {0}", assemblyPath);
                    return;
                }

                // 加载程序集
                // Load assembly.
                Assembly assembly = Assembly.LoadFrom(assemblyPath);

                // 查找实现 IRevitCommand 接口的类型
                // Find types that implement the IRevitCommand interface.
                foreach (Type type in assembly.GetTypes())
                {
                    if (typeof(RevitMCPSDK.API.Interfaces.IRevitCommand).IsAssignableFrom(type) &&
                        !type.IsInterface &&
                        !type.IsAbstract &&
                        // Framework tool adapters are built by ToolRegistry.CreateCommands, not here.
                        (type.GetConstructor(Type.EmptyTypes) != null ||
                         type.GetConstructor(new[] { typeof(UIApplication) }) != null))
                    {
                        try
                        {
                            // 创建命令实例
                            // Create a command instance.
                            RevitMCPSDK.API.Interfaces.IRevitCommand command;

                            // 检查命令是否实现了可初始化接口
                            // Check whether the command implements the initializable interface.
                            if (typeof(IRevitCommandInitializable).IsAssignableFrom(type))
                            {
                                // 创建实例并初始化
                                // Create instance and initialize.
                                command = (IRevitCommand)Activator.CreateInstance(type);
                                ((IRevitCommandInitializable)command).Initialize(_uiApplication);
                            }
                            else
                            {
                                // 尝试查找接受 UIApplication 的构造函数
                                // Try searching for constructors that accept UIApplication.
                                var constructor = type.GetConstructor(new[] { typeof(UIApplication) });
                                if (constructor != null)
                                {
                                    command = (IRevitCommand)constructor.Invoke(new object[] { _uiApplication });
                                }
                                else
                                {
                                    // 使用无参构造函数
                                    // Use a parameterless constructor.
                                    command = (IRevitCommand)Activator.CreateInstance(type);
                                }
                            }

                            // 检查命令名称是否与配置匹配
                            // Check whether the command name matches the configuration.
                            if (command.CommandName == config.CommandName)
                            {
                                _commandRegistry.RegisterCommand(command);
                                _logger.Info("创建命令实例失败 [{0}]: {1}\nFailed to create command instance [{0}]: {1}",
                                    command.CommandName, Path.GetFileName(assemblyPath));
                                break; // 找到匹配的命令后退出循环 - Exit the loop after finding a matching command.
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("创建命令实例失败 [{0}]: {1}\nFailed to create command instance [{0}]: {1}", type.FullName, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("加载命令程序集失败: {0}\nFailed to load command assembly: {0}", ex.Message);
            }
        }
    }
}
