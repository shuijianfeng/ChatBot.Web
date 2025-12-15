using ChatBot.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// MCP 工具信息
    /// </summary>
    public class McpToolInfo
    {
        public string ServerName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public JsonElement? InputSchema { get; set; }
    }

    /// <summary>
    /// MCP 客户端管理器接口
    /// </summary>
    public interface IMcpClientManager : IAsyncDisposable
    {
        /// <summary>
        /// 初始化所有 MCP 客户端连接
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有可用工具列表
        /// </summary>
        Task<IList<McpToolInfo>> GetAllToolsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 调用指定工具
        /// </summary>
        Task<string> CallToolAsync(string toolName, string arguments, CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查工具是否是 MCP 工具
        /// </summary>
        bool IsMcpTool(string toolName);

        /// <summary>
        /// 是否已启用
        /// </summary>
        bool IsEnabled { get; }
    }

    /// <summary>
    /// MCP 客户端管理器实现
    /// </summary>
    public class McpClientManager : IMcpClientManager
    {
        private readonly McpSettings _settings;
        private readonly ILogger<McpClientManager> _logger;
        private readonly ConcurrentDictionary<string, McpClient> _clients = new();
        private readonly ConcurrentDictionary<string, McpToolInfo> _toolsCache = new();
        private bool _initialized;
        private readonly object _initLock = new();

        public bool IsEnabled => _settings.Enabled;

        public McpClientManager(IOptions<McpSettings> settings, ILogger<McpClientManager> logger)
        {
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized || !_settings.Enabled)
                return;

            lock (_initLock)
            {
                if (_initialized)
                    return;
                _initialized = true;
            }

            _logger.LogInformation("正在初始化 MCP 客户端管理器...");

            foreach (var serverConfig in _settings.Servers)
            {
                try
                {
                    await ConnectToServerAsync(serverConfig, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "连接 MCP 服务器 '{ServerName}' 失败", serverConfig.Name);
                }
            }

            _logger.LogInformation("MCP 客户端管理器初始化完成，共连接 {Count} 个服务器，加载 {ToolCount} 个工具",
                _clients.Count, _toolsCache.Count);
        }

        private async Task ConnectToServerAsync(McpServerConfig serverConfig, CancellationToken cancellationToken)
        {
            McpClient client;

            if (serverConfig.TransportType == McpTransportType.Sse)
            {
                // SSE/HTTP 传输 - 连接远程服务器
                if (string.IsNullOrEmpty(serverConfig.Url))
                {
                    throw new InvalidOperationException($"MCP 服务器 '{serverConfig.Name}' 配置为 SSE 传输，但未提供 URL");
                }

                _logger.LogInformation("正在连接 MCP 服务器 (SSE): {ServerName} ({Url})",
                    serverConfig.Name, serverConfig.Url);

                // 使用 HttpClientTransport 连接远程 SSE/HTTP 服务器
                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(serverConfig.Url)
                };

                // 添加自定义 Headers（如 Authorization）
                if (serverConfig.Headers != null)
                {
                    transportOptions.AdditionalHeaders = serverConfig.Headers;
                }

                var httpTransport = new HttpClientTransport(transportOptions);
                client = await McpClient.CreateAsync(httpTransport, cancellationToken: cancellationToken);
            }
            else
            {
                // Stdio 传输 - 启动本地进程
                _logger.LogInformation("正在连接 MCP 服务器 (Stdio): {ServerName} (命令: {Command} {Arguments})",
                    serverConfig.Name, serverConfig.Command, string.Join(" ", serverConfig.Arguments));

                var transportOptions = new StdioClientTransportOptions
                {
                    Name = serverConfig.Name,
                    Command = serverConfig.Command,
                    Arguments = serverConfig.Arguments.ToList(),
                };

                // 设置工作目录
                if (!string.IsNullOrEmpty(serverConfig.WorkingDirectory))
                {
                    transportOptions.WorkingDirectory = serverConfig.WorkingDirectory;
                }

                // 设置环境变量
                if (serverConfig.EnvironmentVariables != null && serverConfig.EnvironmentVariables.Count > 0)
                {
                    transportOptions.EnvironmentVariables = serverConfig.EnvironmentVariables;
                }

                var transport = new StdioClientTransport(transportOptions);
                client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            }

            _clients[serverConfig.Name] = client;

            // 获取工具列表并缓存
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            foreach (var tool in tools)
            {
                var toolInfo = new McpToolInfo
                {
                    ServerName = serverConfig.Name,
                    Name = tool.Name,
                    Description = tool.Description ?? string.Empty,
                    InputSchema = tool.JsonSchema
                };
                _toolsCache[tool.Name] = toolInfo;
                _logger.LogDebug("加载 MCP 工具: {ToolName} from {ServerName}", tool.Name, serverConfig.Name);
            }

            _logger.LogInformation("成功连接 MCP 服务器 '{ServerName}'，加载了 {ToolCount} 个工具",
                serverConfig.Name, tools.Count);
        }

        public Task<IList<McpToolInfo>> GetAllToolsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IList<McpToolInfo>>(_toolsCache.Values.ToList());
        }

        public bool IsMcpTool(string toolName)
        {
            return _toolsCache.ContainsKey(toolName);
        }

        public async Task<string> CallToolAsync(string toolName, string arguments, CancellationToken cancellationToken = default)
        {
            if (!_toolsCache.TryGetValue(toolName, out var toolInfo))
            {
                return $"错误：未找到 MCP 工具 '{toolName}'";
            }

            if (!_clients.TryGetValue(toolInfo.ServerName, out var client))
            {
                return $"错误：MCP 服务器 '{toolInfo.ServerName}' 未连接";
            }

            try
            {
                _logger.LogInformation("调用 MCP 工具: {ToolName} with arguments: {Arguments}", toolName, arguments);

                // 解析参数
                Dictionary<string, object?> args = new();
                if (!string.IsNullOrWhiteSpace(arguments) && arguments != "{}")
                {
                    try
                    {
                        args = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments) ?? new();
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "解析工具参数失败: {Arguments}", arguments);
                    }
                }

                var result = await client.CallToolAsync(toolName, args, cancellationToken: cancellationToken);

                // 提取结果文本
                var textContents = result.Content
                    .Where(c => c is TextContentBlock)
                    .Cast<TextContentBlock>()
                    .Select(c => c.Text);

                var resultText = string.Join("\n", textContents);
                _logger.LogInformation("MCP 工具 '{ToolName}' 执行完成", toolName);

                return string.IsNullOrEmpty(resultText) ? "工具执行成功（无返回内容）" : resultText;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 MCP 工具 '{ToolName}' 失败", toolName);
                return $"错误：调用 MCP 工具失败 - {ex.Message}";
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients.Values)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "释放 MCP 客户端时发生错误");
                }
            }
            _clients.Clear();
            _toolsCache.Clear();
        }
    }
}
