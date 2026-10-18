using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// The person's choice of approval mode, in %APPDATA%\revit-mcp\approval-mode.json. The broker re-reads it before
    /// every command, so a change applies at once. Only this add-in's Settings window writes it: no MCP tool can, so an
    /// AI can't switch its own approvals off.
    /// </summary>
    public static class ApprovalModeStore
    {
        public const string AskFirst = "ask_first";
        public const string Auto = "auto";
        public const string LookOnly = "look_only";

        public static string FilePath() => Path.Combine(BrokerToken.StateDirectory(), "approval-mode.json");

        /// <summary>The saved mode, or ask_first when none was chosen or the file is unreadable.</summary>
        public static string Read()
        {
            try
            {
                var file = FilePath();
                if (!File.Exists(file)) return AskFirst;
                var mode = (string)JObject.Parse(File.ReadAllText(file))["mode"];
                return mode == Auto || mode == LookOnly ? mode : AskFirst;
            }
            catch
            {
                return AskFirst;
            }
        }

        /// <summary>Saves the mode (write to a temp file, then replace, so the broker never reads half a file).</summary>
        public static void Write(string mode)
        {
            if (mode != AskFirst && mode != Auto && mode != LookOnly)
                throw new ArgumentException("Unknown approval mode: " + mode, nameof(mode));
            var file = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var json = new JObject
            {
                ["mode"] = mode,
                ["changedBy"] = "revit-settings:" + Environment.UserName,
                ["changedAt"] = DateTime.UtcNow.ToString("o")
            }.ToString(Formatting.Indented);
            var temp = file + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
        }
    }
}
