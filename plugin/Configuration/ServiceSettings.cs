using Newtonsoft.Json;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>服务设置类</para>
    /// <para>Service settings.</para>
    /// </summary>
    public class ServiceSettings
    {
        /// <summary>
        /// <para>日志级别</para>
        /// <para>Log level.</para>
        /// </summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// <para>socket服务端口 (legacy, unused since the plugin dials the broker out).</para>
        /// <para>Socket service port. Legacy: kept for config compatibility only —
        /// the plugin now connects OUT to the broker and does not bind a port.</para>
        /// </summary>
        [JsonProperty("port")]
        public int Port { get; set; } = 8080;

        /// <summary>
        /// <para>Broker WebSocket URL. Loopback only.</para>
        /// </summary>
        [JsonProperty("brokerUrl")]
        public string BrokerUrl { get; set; } = "ws://127.0.0.1:8090";

        /// <summary>
        /// <para>是否允许为 ui-bound 命令自动激活目标文档 (Fase 4 Opção B)。默认关闭。</para>
        /// <para>Whether a ui-bound command targeting a non-active document may
        /// auto-activate it (OpenAndActivateDocument), run, then restore the
        /// previous document. Off by default: switching documents under the user
        /// is a reliable source of bugs and surprise.</para>
        /// </summary>
        [JsonProperty("allowAutoActivate")]
        public bool AllowAutoActivate { get; set; } = false;

        /// <summary>
        /// <para>是否允许对非激活文档执行写操作。默认关闭 (只读)。</para>
        /// <para>Whether write commands may run against a document that is not the
        /// active one. Off by default: background documents are read-only unless
        /// the operator explicitly opts in.</para>
        /// </summary>
        [JsonProperty("allowBackgroundWrites")]
        public bool AllowBackgroundWrites { get; set; } = false;
    }
}
