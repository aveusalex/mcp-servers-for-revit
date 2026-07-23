using System;
using System.IO;
using System.Security.Cryptography;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// Reads (or first-run generates) the shared handshake token used to
    /// authenticate with the broker. The token lives at
    /// <c>%APPDATA%\revit-mcp\broker-token</c> and is shared, byte-for-byte, with
    /// the broker daemon and the MCP server. Because the plugin now connects on
    /// its own with no manual click, the token is not optional: it is the only
    /// gate between an arbitrary local process and the Revit API.
    /// </summary>
    public static class BrokerToken
    {
        public static string StateDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "revit-mcp");
        }

        public static string TokenFilePath() => Path.Combine(StateDirectory(), "broker-token");

        /// <summary>
        /// Return the shared token, creating and persisting one if this is the
        /// first process to look. Whichever of plugin/broker/server runs first
        /// wins; the others read the same value back.
        /// </summary>
        public static string LoadOrCreate()
        {
            string dir = StateDirectory();
            Directory.CreateDirectory(dir);
            string file = TokenFilePath();

            try
            {
                if (File.Exists(file))
                {
                    string existing = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrEmpty(existing))
                        return existing;
                }
            }
            catch
            {
                // fall through to regeneration
            }

            string token = GenerateToken();
            File.WriteAllText(file, token);
            return token;
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
