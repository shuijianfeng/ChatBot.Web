using ChatBot.Models;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static ChatBot.Models.GeminiChunkResponse;
using AngleSharp.Dom;
using DocumentFormat.OpenXml.EMMA;
using DocumentFormat.OpenXml.Vml;
using System.Linq;
using System.Collections.Concurrent;

namespace ChatBot.Web.Services
{
    public class JinaSearch
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly JinaSearchResult _result;
        private readonly Dictionary<string, string> _nameToCode;
        private readonly Dictionary<string, string> _codeToName;
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        private volatile bool _isInitialized = false;

        // 缓存常用的 HashSet 以提高查找性能
        private readonly HashSet<string> _processedUrls = new(StringComparer.OrdinalIgnoreCase);

        public JinaSearchResult Result => _result;

        public JinaSearch(IHttpClientFactory httpClientFactory)
        {
            _result = new JinaSearchResult();
            _httpClientFactory = httpClientFactory;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            _nameToCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _codeToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 移除构造函数中的阻塞调用
            // InitializeAsync().GetAwaiter().GetResult();
        }

        // 异步初始化方法，避免构造函数中的阻塞调用
        public async Task<JinaSearch> InitializeAsync()
        {
            if (_isInitialized) return this;

            await _initSemaphore.WaitAsync();
            try
            {
                if (_isInitialized) return this;

                string stationData = await GetStationDataAsync();
                ParseStationData(stationData, _nameToCode, _codeToName);
                _isInitialized = true;
                return this;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        // 改进的查询扩展方法，添加缓存和更好的错误处理
        private async Task<string> ExpandQuery(string originalQuery)
        {
            if (string.IsNullOrWhiteSpace(originalQuery) || originalQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3)
            {
                return originalQuery;
            }

            var apiKey = Environment.GetEnvironmentVariable("GeminiKey");
            if (string.IsNullOrEmpty(apiKey)) return originalQuery;

            var apiEndpoint = "https://cdsjf.xyz/gemini/v1beta/openai/chat/completions";

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30); // 减少超时时间
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                var requestContent = new
                {
                    model = "gemini-flash-lite-latest",
                    messages = new[]
                    {
                        new { role = "system", content = "你是一个优秀的搜索查询优化专家。请将用户的查询扩展为更详细的搜索词，保持原始查询的核心意图，不超过5个词。直接返回扩展后的查询词，不要包含任何解释或其他文本。" },
                        new { role = "user", content = originalQuery }
                    },
                    temperature = 0.1,
                };

                using var response = await client.PostAsync(apiEndpoint, 
                    new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<OpenAIResponse>(responseStr, _jsonOptions);
                    var expandedQuery = result?.choices?.FirstOrDefault()?.message?.content?.Trim();
                    
                    return !string.IsNullOrWhiteSpace(expandedQuery) ? expandedQuery : originalQuery;
                }
            }
            catch (Exception)
            {
                // 静默失败，返回原始查询
            }

            return originalQuery;
        }

        public async Task<string?> Search(string query, int searchCount = 3, bool isNoCache = false, bool isdirect = true)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            // 可选择启用查询扩展
            // query = await ExpandQuery(query);
            
            _result.Data.Clear();
            _processedUrls.Clear();

            // 并行执行搜索，减少等待时间
            var searchTasks = new []
            {
                //SearchSite(query, "", searchCount, isNoCache, isdirect),           // Google
                SearchSite(query, "baidu.com ", searchCount, isNoCache, isdirect), // Baidu
                SearchSite(query, "ctrip.com ", searchCount, isNoCache, isdirect), // Ctrip
                SearchSite(query, "dianping.com ", searchCount, isNoCache, isdirect) // Dianping
            };

            var results = await Task.WhenAll(searchTasks);

            // 高效合并结果，避免重复数据
            foreach (var result in results)
            {
                if (result?.Data != null)
                {
                    _result.Data.AddRange(result.Data);
                }
            }

            await GetInfoFromGemini(query);
            await Rerank(query);
            return await MergeInfo();
        }

        // 重命名方法以避免重载混淆
        private async Task<JinaSearchResult?> SearchSite(string query, string site, int searchCount = 3, bool isNoCache = false, bool isdirect = true)
        {
            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            if (string.IsNullOrEmpty(apiKey)) return null;

            var apiEndpoint = "https://s.jina.ai";

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5); // 减少超时时间

                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("X-Respond-With", "no-content");

                if (!string.IsNullOrWhiteSpace(site))
                {
                    client.DefaultRequestHeaders.Add("X-Site", site);
                }
                if (isdirect)
                {
                    client.DefaultRequestHeaders.Add("X-Engine", "direct");
                }
                if (isNoCache)
                {
                    client.DefaultRequestHeaders.Add("X-No-Cache", "true");
                }

                var requestContent = new
                {
                    q = query,
                    count = searchCount,
                };

                using var response = await client.PostAsync(apiEndpoint,
                    new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode) return null;

                var responseStr = await response.Content.ReadAsStringAsync();
                var jinaSearchResult = JsonSerializer.Deserialize<JinaSearchResult>(responseStr, _jsonOptions);

                if (jinaSearchResult?.Data == null) return null;

                // URL 解码和去重优化
                var processedData = new List<JinaSearchResultData>();
                foreach (var item in jinaSearchResult.Data)
                {
                    item.Url = Uri.UnescapeDataString(item.Url);
                    
                    // 使用 HashSet 进行高效去重
                    if (!_processedUrls.Contains(item.Url))
                    {
                        _processedUrls.Add(item.Url);
                        processedData.Add(item);
                    }
                }

                jinaSearchResult.Data = processedData;

                if (jinaSearchResult.Data.Count > 0)
                {
                    await Reader(jinaSearchResult, isNoCache, isdirect);
                }

                return jinaSearchResult;
            }
            catch (Exception)
            {
                return null;
            }
        }
        public async Task Reader(JinaSearchResult jinaSearchResult, bool isNoCache = false, bool isdirect = true)
        {
            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            if (string.IsNullOrEmpty(apiKey) || jinaSearchResult?.Data == null) return;

            var apiEndpoint = "https://r.jina.ai";

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);

                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("X-Retain-Images", "none");

                if (isdirect)
                {
                    client.DefaultRequestHeaders.Add("X-Engine", "direct");
                }
                if (isNoCache)
                {
                    client.DefaultRequestHeaders.Add("X-No-Cache", "true");
                }

                // 使用 SemaphoreSlim 限制并发请求数量，避免API限制
                using var semaphore = new SemaphoreSlim(3, 3); // 最多3个并发请求

                var tasks = jinaSearchResult.Data.Select(async item =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var requestContent = new { url = item.Url };
                        
                        using var response = await client.PostAsync(apiEndpoint,
                            new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json"));

                        if (response.IsSuccessStatusCode)
                        {
                            var responseStr = await response.Content.ReadAsStringAsync();
                            var jinaReaderResult = JsonSerializer.Deserialize<JinaReaderResult>(responseStr, _jsonOptions);
                            
                            if (jinaReaderResult?.Data?.Content != null)
                            {
                                item.Content = jinaReaderResult.Data.Content;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 静默失败，继续处理其他项目
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                // 静默失败
            }
        }
        public async Task Rerank(string query, int rerankCount = 5)
        {
            // 高效的去重和过滤
            var validData = new List<JinaSearchResultData>();
            var contentHashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _result.Data)
            {
                if (string.IsNullOrWhiteSpace(item.Content)) continue;

                // 使用内容的哈希进行快速去重
                var contentHash = item.Content.GetHashCode(StringComparison.OrdinalIgnoreCase).ToString();
                if (!contentHashSet.Contains(contentHash))
                {
                    contentHashSet.Add(contentHash);
                    validData.Add(item);
                }
            }

            _result.Data = validData;

            if (_result.Data.Count == 0) return;

            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            if (string.IsNullOrEmpty(apiKey)) return;

            var apiEndpoint = "https://api.jina.ai/v1/rerank";

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                var requestContent = new
                {
                    model = "jina-reranker-v2-base-multilingual",
                    query = query,
                    top_n = Math.Min(rerankCount, _result.Data.Count),
                    documents = _result.Data.Select(x => x.Content).ToList()
                };

                using var response = await client.PostAsync(apiEndpoint,
                    new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    var jinaRerankResult = JsonSerializer.Deserialize<JinaRerankResult>(responseStr, _jsonOptions);

                    if (jinaRerankResult?.Results != null)
                    {
                        // 只保留排名靠前的结果
                        var rankedIndexes = new HashSet<int>();
                        var maxResults = Math.Min(jinaRerankResult.Results.Count, rerankCount);
                        
                        for (int i = 0; i < maxResults; i++)
                        {
                            rankedIndexes.Add(jinaRerankResult.Results[i].Index);
                        }

                        _result.Data = _result.Data.Where((item, index) => rankedIndexes.Contains(index)).ToList();
                    }
                }
            }
            catch (Exception)
            {
                // 静默失败，保持原有结果
            }
        }

        public async Task GetInfoFromGemini(string query)
        {
            // 先进行数据清理
            CleanAndDeduplicateData();

            if (_result.Data.Count == 0) return;

            var apiKey = Environment.GetEnvironmentVariable("GeminiKey");
            if (string.IsNullOrEmpty(apiKey)) return;

            var apiEndpoint = "https://cdsjf.xyz/gemini/v1beta/openai/chat/completions";

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                // 使用信号量限制并发
                using var semaphore = new SemaphoreSlim(2, 2);

                var tasks = _result.Data.Select(async item =>
                {
                    if (string.IsNullOrWhiteSpace(item.Content)) return;

                    await semaphore.WaitAsync();
                    try
                    {
                        var systemPrompt = "你是一个优秀的资料整理专家，能够准确提取与特定查询相关的信息。";
                        var userPrompt = $"请从以下<info></info>中提取与查询\"{query}\"相关信息,保持尽可能多的原始信息和细节。如果内容与查询无关，请回答: [NOT]\n\n<info>\n{item.Content}\n</info>";

                        var requestContent = new
                        {
                            model = "gemini-flash-lite-latest",
                            messages = new[]
                            {
                                new { role = "system", content = systemPrompt },
                                new { role = "user", content = userPrompt }
                            },
                            temperature = 0.1,
                        };

                        var originalContent = item.Content;
                        item.Content = string.Empty;
                        item.Description = string.Empty;

                        using var response = await client.PostAsync(apiEndpoint,
                            new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json"));

                        if (response.IsSuccessStatusCode)
                        {
                            var responseStr = await response.Content.ReadAsStringAsync();
                            var geminiResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseStr, _jsonOptions);
                            var content = geminiResponse?.choices?.FirstOrDefault()?.message?.content;

                            if (!string.IsNullOrWhiteSpace(content) && !content.Contains("[NOT]"))
                            {
                                item.Content = content;
                            }
                            else
                            {
                                // 如果 Gemini 认为不相关，恢复原内容以供后续处理决定
                                item.Content = originalContent;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 静默失败
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
                
                // 移除空内容
                _result.Data = _result.Data.Where(x => !string.IsNullOrWhiteSpace(x.Content)).ToList();
            }
            catch (Exception)
            {
                // 静默失败
            }
        }

        // 数据清理和去重的辅助方法
        private void CleanAndDeduplicateData()
        {
            var cleanData = new List<JinaSearchResultData>();
            var contentHashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var urlHashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _result.Data)
            {
                if (string.IsNullOrWhiteSpace(item.Content) || string.IsNullOrWhiteSpace(item.Url))
                    continue;

                var contentHash = item.Content.GetHashCode(StringComparison.OrdinalIgnoreCase).ToString();
                
                if (!contentHashSet.Contains(contentHash) && !urlHashSet.Contains(item.Url))
                {
                    contentHashSet.Add(contentHash);
                    urlHashSet.Add(item.Url);
                    cleanData.Add(item);
                }
            }

            _result.Data = cleanData;
        }

        public async Task<string> MergeInfo()
        {
            if (_result.Data.Count == 0) return string.Empty;

            var apiKey = Environment.GetEnvironmentVariable("GeminiKey");
            if (string.IsNullOrEmpty(apiKey)) return string.Empty;

            var apiEndpoint = "https://cdsjf.xyz/gemini/v1beta/openai/chat/completions";

            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                // 使用 StringBuilder 优化字符串构建
                var infoBuilder = new StringBuilder();
                foreach (var item in _result.Data)
                {
                    if (string.IsNullOrWhiteSpace(item.Content)) continue;
                    
                    infoBuilder.AppendLine($"Url: {item.Url}")
                              .AppendLine($"Title: {item.Title}")
                              .AppendLine("Content：")
                              .AppendLine(item.Content)
                              .AppendLine()
                              .AppendLine();
                }

                var systemPrompt = "你是一个优秀的资料整合专家，善于将多个来源的信息融合为连贯、详细且易于理解的文本。请确保保留所有重要信息，并正确引用来源。";
                var userPrompt = $@"1、整合<info></info>中资料,保持尽可能多的原始信息和细节。
2、在描述中使用以空格符作为间隔的引用标记，引用信息来源（格式：[1] [3] [11]）,并且在内容末尾提供完整引用来源列表，格式为：

 [1]: https://example.com 

 [2]: https://example.com
3、组织信息时，请按主题或时间顺序结构化内容，避免简单堆砌。
4、如有相互矛盾的信息，请一并呈现并标明来源。

<info>
{infoBuilder}
</info>";

                var requestContent = new
                {
                    model = "gemini-flash-lite-latest",
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    temperature = 0.1,
                };

                using var response = await client.PostAsync(apiEndpoint,
                    new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    var geminiResponse = JsonSerializer.Deserialize<OpenAIResponse>(responseStr, _jsonOptions);
                    var content = geminiResponse?.choices?.FirstOrDefault()?.message?.content;

                    return !string.IsNullOrWhiteSpace(content) ? content : string.Empty;
                }
            }
            catch (Exception)
            {
                // 静默失败
            }

            return string.Empty;
        }

        // 优化后的车票搜索方法
        public async Task<string?> SearchTrainTicket(string Startingplace, string Arrivalplace, string date)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            var startingPlaceCode = _nameToCode.GetValueOrDefault(Startingplace);
            var arrivalPlaceCode = _nameToCode.GetValueOrDefault(Arrivalplace);

            if (string.IsNullOrEmpty(startingPlaceCode) || string.IsNullOrEmpty(arrivalPlaceCode))
            {
                return "未找到车站代码";
            }

            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true,
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                };

                using var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromMinutes(1);

                // 设置请求头
                var headers = new Dictionary<string, string>
                {
                    ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8",
                    ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8",
                    ["Cache-Control"] = "max-age=0",
                    ["Connection"] = "keep-alive",
                    ["Upgrade-Insecure-Requests"] = "1"
                };

                foreach (var header in headers)
                {
                    client.DefaultRequestHeaders.Add(header.Key, header.Value);
                }
                client.DefaultRequestHeaders.Referrer = new Uri("https://kyfw.12306.cn/");

                // 构建查询参数
                var queryParams = $"leftTicketDTO.train_date={date}&leftTicketDTO.from_station={startingPlaceCode}&leftTicketDTO.to_station={arrivalPlaceCode}&purpose_codes=ADULT";
                var logUri = $"https://kyfw.12306.cn/otn/leftTicket/log?{queryParams}";
                var queryUri = $"https://kyfw.12306.cn/otn/leftTicket/query?{queryParams}";

                // 先请求日志接口
                await client.GetStringAsync(logUri);

                var response = await client.GetAsync(queryUri);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var trainData = JsonSerializer.Deserialize<Train>(content, _jsonOptions);

                    if (trainData?.data?.result?.Count > 0)
                    {
                        return FormatTrainData(trainData, _codeToName);
                    }
                    else
                    {
                        return "未找到符合条件的列车";
                    }
                }
                else
                {
                    return $"请求失败：{response.StatusCode} - {await response.Content.ReadAsStringAsync()}";
                }
            }
            catch (Exception ex)
            {
                return $"发生错误：{ex.Message}";
            }
        }

        // 格式化列车数据的方法，使用 StringBuilder 优化
        private static string FormatTrainData(Train trainData, Dictionary<string, string> codeToName)
        {
            var sb = new StringBuilder("列车查询结果：\n");

            foreach (var item in trainData.data.result)
            {
                var segments = item.Split('|');
                if (segments.Length <= 30) continue;

                var trainCode = segments[3]; // 车次
                var fromStation = codeToName.GetValueOrDefault(segments[4], segments[4]); // 始发站
                var toStation = codeToName.GetValueOrDefault(segments[5], segments[5]); // 终点站
                var fromStation1 = codeToName.GetValueOrDefault(segments[6], segments[6]); // 出发站
                var toStation1 = codeToName.GetValueOrDefault(segments[7], segments[7]); // 到达站
                var startTime = segments[8]; // 出发时间
                var arriveTime = segments[9]; // 到达时间
                var duration = segments[10]; // 历时

                // 座位信息
                var ywSeat = segments[28]; // 硬卧
                var yzSeat = segments[29]; // 硬座
                var rwSeat = segments[23]; // 软卧
                var tdSeat = segments[32]; // 特等座
                var swzSeat = string.IsNullOrEmpty(segments[25]) ? "无" : segments[25]; // 商务座
                var zyDeat = segments[31]; // 一等座
                var edSeat = segments[30]; // 二等座

                sb.AppendLine($"车次: {trainCode}")
                  .AppendLine($"始发/终点: {fromStation} -> {toStation}")
                  .AppendLine($"出发/到达: {fromStation1} -> {toStation1}")
                  .AppendLine($"时间: {startTime} -> {arriveTime} 历时: {duration}")
                  .AppendLine($"座位: 商务: {swzSeat}，特等：{tdSeat}, 一等: {zyDeat}, 二等: {edSeat}")
                  .AppendLine($"      软卧: {rwSeat},  硬卧: {ywSeat}, 硬座: {yzSeat}")
                  .AppendLine("----------------------");
            }

            return sb.ToString();
        }

        private static async Task<string> GetStationDataAsync()
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            return await client.GetStringAsync("https://kyfw.12306.cn/otn/resources/js/framework/station_name.js");
        }

        // 解析车站数据，使用编译的正则表达式提高性能
        private static readonly Regex StationRegex = new(@"@([a-z]+)\|([^|]+)\|([A-Z]+)\|", RegexOptions.Compiled);
        
        private static void ParseStationData(string data, Dictionary<string, string> nameToCode, Dictionary<string, string> codeToName)
        {
            var matches = StationRegex.Matches(data);

            foreach (Match match in matches)
            {
                var name = match.Groups[2].Value;  // 站名
                var code = match.Groups[3].Value;  // 站代码

                nameToCode.TryAdd(name, code);
                codeToName.TryAdd(code, name);
            }
        }

        public static string getsegstr(string as_str, char as_fgstr, int ai_num)
        {
            if (string.IsNullOrEmpty(as_str) || ai_num <= 0) return string.Empty;
            
            ReadOnlySpan<char> strSpan = as_str.AsSpan();
            var index = strSpan.IndexOf(as_fgstr);
            
            if (index < 0)
            {
                return ai_num == 1 ? as_str : string.Empty;
            }

            int currentSegment = 1;
            int currentIndex = 0;

            while (index >= 0 && currentSegment < ai_num)
            {
                currentIndex += index + 1;
                strSpan = strSpan.Slice(index + 1);
                index = strSpan.IndexOf(as_fgstr);
                currentSegment++;
            }

            if (currentSegment == ai_num)
            {
                if (index >= 0)
                {
                    return as_str.Substring(currentIndex, index);
                }
                else
                {
                    return as_str.Substring(currentIndex);
                }
            }

            return string.Empty;
        }

        public void Dispose()
        {
            _initSemaphore?.Dispose();
        }
    }

    // TextSplitter 类优化
    public class TextSplitter
    {
        private readonly int _maxChunkSize;
        private readonly int _overlap;
        private static readonly Regex SentenceRegex = new(@"(?<=[.!?。！？])\s+", RegexOptions.Compiled);

        public TextSplitter(int maxChunkSize = 512, int overlap = 50)
        {
            _maxChunkSize = maxChunkSize;
            _overlap = overlap;
        }

        public IEnumerable<string> Split(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var currentChunk = new StringBuilder();
            var chunks = new List<string>();

            foreach (var paragraph in paragraphs)
            {
                if (paragraph.Length > _maxChunkSize)
                {
                    if (currentChunk.Length > 0)
                    {
                        chunks.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }

                    var sentences = SplitIntoSentences(paragraph);
                    var sentenceChunks = ChunkSentences(sentences);
                    chunks.AddRange(sentenceChunks);
                }
                else
                {
                    if (currentChunk.Length + paragraph.Length > _maxChunkSize)
                    {
                        chunks.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }

                    if (currentChunk.Length > 0)
                    {
                        currentChunk.AppendLine();
                    }
                    currentChunk.Append(paragraph);
                }
            }

            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
            }

            foreach (var chunk in AddOverlap(chunks))
            {
                yield return chunk;
            }
        }

        private static IEnumerable<string> SplitIntoSentences(string text)
        {
            return SentenceRegex.Split(text).Where(s => !string.IsNullOrWhiteSpace(s));
        }

        private IEnumerable<string> ChunkSentences(IEnumerable<string> sentences)
        {
            var currentChunk = new StringBuilder();
            var chunks = new List<string>();

            foreach (var sentence in sentences)
            {
                if (currentChunk.Length + sentence.Length > _maxChunkSize)
                {
                    if (currentChunk.Length > 0)
                    {
                        chunks.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }

                    if (sentence.Length > _maxChunkSize)
                    {
                        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        chunks.AddRange(ChunkByWords(words));
                        continue;
                    }
                }

                if (currentChunk.Length > 0)
                {
                    currentChunk.Append(" ");
                }
                currentChunk.Append(sentence);
            }

            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
            }

            return chunks;
        }

        private IEnumerable<string> ChunkByWords(string[] words)
        {
            var currentChunk = new StringBuilder();

            foreach (var word in words)
            {
                if (currentChunk.Length + word.Length + 1 > _maxChunkSize)
                {
                    if (currentChunk.Length > 0)
                    {
                        yield return currentChunk.ToString();
                        currentChunk.Clear();
                    }
                }

                if (currentChunk.Length > 0)
                {
                    currentChunk.Append(" ");
                }
                currentChunk.Append(word);
            }

            if (currentChunk.Length > 0)
            {
                yield return currentChunk.ToString();
            }
        }

        private IEnumerable<string> AddOverlap(List<string> chunks)
        {
            if (_overlap <= 0 || chunks.Count <= 1)
            {
                foreach (var chunk in chunks)
                    yield return chunk;
                yield break;
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i > 0)
                {
                    var previousChunk = chunks[i - 1];
                    var overlapFromPrevious = previousChunk.Length <= _overlap
                        ? previousChunk
                        : previousChunk[^_overlap..];

                    yield return overlapFromPrevious + chunks[i];
                }
                else
                {
                    yield return chunks[i];
                }
            }
        }
    }

    public class RerankItem
    {
        public JinaSearchResultData jinaSearchResultData { get; set; } = new();
        public int beging { get; set; }
        public int end { get; set; }
    }

    public class Train
    {
        public Traindata data { get; set; } = new();

        public class Traindata
        {
            public List<string> result { get; set; } = new();
        }
    }
}
