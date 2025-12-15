using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// 携程旅游搜索服务
    /// 提供酒店、机票、景点门票、旅游产品搜索功能
    /// </summary>
    public class CtripSearch : IDisposable
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;

        public CtripSearch(IHttpClientFactory httpClientFactory, ILogger logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        #region 酒店搜索

        /// <summary>
        /// 搜索携程酒店信息
        /// </summary>
        /// <param name="city">城市名称</param>
        /// <param name="checkInDate">入住日期 (YYYY-MM-DD)</param>
        /// <param name="checkOutDate">离店日期 (YYYY-MM-DD)</param>
        /// <param name="keyword">关键词（可选，如酒店名称、地标）</param>
        /// <returns>格式化的酒店搜索结果</returns>
        public async Task<string> SearchHotel(string city, string checkInDate, string checkOutDate, string? keyword = null)
        {
            try
            {
                _logger.LogInformation("搜索携程酒店: 城市={City}, 入住={CheckIn}, 离店={CheckOut}, 关键词={Keyword}",
                    city, checkInDate, checkOutDate, keyword);

                // 直接使用 Jina Reader 网页搜索（携程 H5 API 需要认证，不可用）
                return await SearchHotelViaWeb(city, checkInDate, checkOutDate, keyword);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "携程酒店搜索失败");
                return $"搜索酒店时发生错误: {ex.Message}\n\n" +
                       $"🔗 [在携程查看 {city} 酒店](https://hotels.ctrip.com/hotels/list?city={HttpUtility.UrlEncode(city)})";
            }
        }

        /// <summary>
        /// 通过网页搜索酒店（降级方案）
        /// </summary>
        private async Task<string> SearchHotelViaWeb(string city, string checkInDate, string checkOutDate, string? keyword)
        {
            try
            {
                var client = CreateHttpClient();
                var searchQuery = string.IsNullOrEmpty(keyword) ? $"{city}酒店" : $"{city} {keyword} 酒店";

                // 使用 Jina Reader API 获取携程酒店页面内容
                var jinaApiKey = Environment.GetEnvironmentVariable("JinaAiApi");
                if (!string.IsNullOrEmpty(jinaApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jinaApiKey}");
                    client.DefaultRequestHeaders.Add("X-Respond-With", "markdown");

                    var ctripUrl = $"https://hotels.ctrip.com/hotels/list?city={HttpUtility.UrlEncode(city)}&checkin={checkInDate}&checkout={checkOutDate}";
                    var jinaUrl = $"https://r.jina.ai/{ctripUrl}";

                    var response = await client.GetAsync(jinaUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        return ExtractHotelInfo(content, city, checkInDate, checkOutDate);
                    }
                }

                return $"未能获取 {city} 的酒店信息。建议访问 https://hotels.ctrip.com 查询。\n" +
                       $"入住日期: {checkInDate}\n离店日期: {checkOutDate}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "网页搜索酒店失败");
                return $"搜索酒店时发生错误: {ex.Message}";
            }
        }

        private string FormatHotelResults(string jsonContent, string city, string checkInDate, string checkOutDate)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                var sb = new StringBuilder();
                sb.AppendLine($"## 🏨 {city} 酒店搜索结果");
                sb.AppendLine($"📅 入住: {checkInDate} | 离店: {checkOutDate}");
                sb.AppendLine();

                if (root.TryGetProperty("hotelList", out var hotelList) && hotelList.ValueKind == JsonValueKind.Array)
                {
                    int count = 0;
                    foreach (var hotel in hotelList.EnumerateArray())
                    {
                        if (count >= 10) break;

                        var name = hotel.TryGetProperty("hotelName", out var n) ? n.GetString() : "未知酒店";
                        var address = hotel.TryGetProperty("address", out var a) ? a.GetString() : "";
                        var score = hotel.TryGetProperty("score", out var s) ? s.GetDouble().ToString("F1") : "暂无";
                        var price = hotel.TryGetProperty("price", out var p) ? $"¥{p.GetInt32()}" : "价格待询";
                        var star = hotel.TryGetProperty("star", out var st) ? GetStarDisplay(st.GetInt32()) : "";

                        sb.AppendLine($"### {++count}. {name} {star}");
                        sb.AppendLine($"- 📍 地址: {address}");
                        sb.AppendLine($"- ⭐ 评分: {score}");
                        sb.AppendLine($"- 💰 参考价: {price}/晚");
                        sb.AppendLine();
                    }

                    if (count == 0)
                    {
                        sb.AppendLine("暂无符合条件的酒店，请尝试调整搜索条件。");
                    }
                }
                else
                {
                    sb.AppendLine("暂无酒店数据，请访问携程官网查询。");
                }

                sb.AppendLine($"\n🔗 [查看更多酒店](https://hotels.ctrip.com/hotels/list?city={HttpUtility.UrlEncode(city)})");
                return sb.ToString();
            }
            catch
            {
                return $"解析酒店数据失败，请访问 https://hotels.ctrip.com 查询 {city} 酒店。";
            }
        }

        private string ExtractHotelInfo(string markdownContent, string city, string checkInDate, string checkOutDate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 🏨 {city} 酒店信息");
            sb.AppendLine($"📅 入住: {checkInDate} | 离店: {checkOutDate}");
            sb.AppendLine();

            // 提取关键信息
            var lines = markdownContent.Split('\n');
            int hotelCount = 0;

            foreach (var line in lines)
            {
                if (hotelCount >= 10) break;

                // 简单提取包含价格和酒店名称的行
                if (line.Contains("¥") || line.Contains("酒店") || line.Contains("评分"))
                {
                    sb.AppendLine(line.Trim());
                    if (line.Contains("¥")) hotelCount++;
                }
            }

            if (hotelCount == 0)
            {
                sb.AppendLine("未能解析酒店详情，请直接访问携程网站查询。");
            }

            sb.AppendLine($"\n🔗 [查看更多酒店](https://hotels.ctrip.com/hotels/list?city={HttpUtility.UrlEncode(city)})");
            return sb.ToString();
        }

        private static string GetStarDisplay(int star)
        {
            return star switch
            {
                5 => "⭐⭐⭐⭐⭐",
                4 => "⭐⭐⭐⭐",
                3 => "⭐⭐⭐",
                2 => "⭐⭐",
                _ => ""
            };
        }

        #endregion

        #region 机票搜索

        /// <summary>
        /// 搜索携程机票信息
        /// </summary>
        /// <param name="departure">出发城市</param>
        /// <param name="arrival">到达城市</param>
        /// <param name="date">出发日期 (YYYY-MM-DD)</param>
        /// <param name="isRoundTrip">是否往返</param>
        /// <returns>格式化的机票搜索结果</returns>
        public async Task<string> SearchFlight(string departure, string arrival, string date, bool isRoundTrip = false)
        {
            try
            {
                _logger.LogInformation("搜索携程机票: {Departure} → {Arrival}, 日期={Date}, 往返={IsRoundTrip}",
                    departure, arrival, date, isRoundTrip);

                var client = CreateHttpClient();

                // 使用 Jina Reader API 获取携程机票页面
                var jinaApiKey = Environment.GetEnvironmentVariable("JinaAiApi");
                if (!string.IsNullOrEmpty(jinaApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jinaApiKey}");
                    client.DefaultRequestHeaders.Add("X-Respond-With", "markdown");

                    var tripType = isRoundTrip ? "RT" : "OW";
                    var ctripUrl = $"https://flights.ctrip.com/online/list/oneway-{HttpUtility.UrlEncode(departure)}-{HttpUtility.UrlEncode(arrival)}?depdate={date}";
                    var jinaUrl = $"https://r.jina.ai/{ctripUrl}";

                    var response = await client.GetAsync(jinaUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        return ExtractFlightInfo(content, departure, arrival, date, isRoundTrip);
                    }
                }

                return GenerateFlightSearchGuide(departure, arrival, date, isRoundTrip);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索机票失败");
                return $"搜索机票时发生错误: {ex.Message}\n\n" + GenerateFlightSearchGuide(departure, arrival, date, isRoundTrip);
            }
        }

        private string ExtractFlightInfo(string markdownContent, string departure, string arrival, string date, bool isRoundTrip)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## ✈️ {departure} → {arrival} 机票信息");
            sb.AppendLine($"📅 出发日期: {date} | 类型: {(isRoundTrip ? "往返" : "单程")}");
            sb.AppendLine();

            // 提取航班信息
            var lines = markdownContent.Split('\n');
            int flightCount = 0;

            foreach (var line in lines)
            {
                if (flightCount >= 15) break;

                // 提取包含航班号、时间、价格的行
                if (Regex.IsMatch(line, @"([A-Z]{2}\d{3,4}|航空|起飞|到达|¥|\d{2}:\d{2})"))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length > 5)
                    {
                        sb.AppendLine(trimmed);
                        if (line.Contains("¥") || Regex.IsMatch(line, @"[A-Z]{2}\d{3,4}"))
                        {
                            flightCount++;
                        }
                    }
                }
            }

            if (flightCount == 0)
            {
                sb.AppendLine("未能解析航班详情，请直接访问携程网站查询。");
            }

            sb.AppendLine();
            sb.AppendLine(GenerateFlightSearchGuide(departure, arrival, date, isRoundTrip));
            return sb.ToString();
        }

        private static string GenerateFlightSearchGuide(string departure, string arrival, string date, bool isRoundTrip)
        {
            var tripType = isRoundTrip ? "往返" : "单程";
            return $"🔗 [在携程查看 {departure}→{arrival} {tripType}机票](https://flights.ctrip.com/online/list/oneway-{HttpUtility.UrlEncode(departure)}-{HttpUtility.UrlEncode(arrival)}?depdate={date})";
        }

        #endregion

        #region 景点门票搜索

        /// <summary>
        /// 搜索携程景点门票
        /// </summary>
        /// <param name="city">城市名称</param>
        /// <param name="keyword">景点关键词（可选）</param>
        /// <returns>格式化的景点门票搜索结果</returns>
        public async Task<string> SearchAttraction(string city, string? keyword = null)
        {
            try
            {
                _logger.LogInformation("搜索携程景点门票: 城市={City}, 关键词={Keyword}", city, keyword);

                var client = CreateHttpClient();
                var searchQuery = string.IsNullOrEmpty(keyword) ? $"{city}景点" : $"{city} {keyword}";

                // 使用 Jina Reader API
                var jinaApiKey = Environment.GetEnvironmentVariable("JinaAiApi");
                if (!string.IsNullOrEmpty(jinaApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jinaApiKey}");
                    client.DefaultRequestHeaders.Add("X-Respond-With", "markdown");

                    var ctripUrl = $"https://you.ctrip.com/sight/{HttpUtility.UrlEncode(city)}.html";
                    var jinaUrl = $"https://r.jina.ai/{ctripUrl}";

                    var response = await client.GetAsync(jinaUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        return ExtractAttractionInfo(content, city, keyword);
                    }
                }

                return GenerateAttractionGuide(city, keyword);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索景点门票失败");
                return $"搜索景点门票时发生错误: {ex.Message}\n\n" + GenerateAttractionGuide(city, keyword);
            }
        }

        private string ExtractAttractionInfo(string markdownContent, string city, string? keyword)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 🎫 {city} 景点门票");
            if (!string.IsNullOrEmpty(keyword))
            {
                sb.AppendLine($"🔍 搜索关键词: {keyword}");
            }
            sb.AppendLine();

            var lines = markdownContent.Split('\n');
            int attractionCount = 0;

            foreach (var line in lines)
            {
                if (attractionCount >= 10) break;

                // 提取景点相关信息
                if (line.Contains("景") || line.Contains("门票") || line.Contains("¥") ||
                    line.Contains("评分") || line.Contains("级") || line.Contains("AAAAA"))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length > 3)
                    {
                        sb.AppendLine(trimmed);
                        attractionCount++;
                    }
                }
            }

            if (attractionCount == 0)
            {
                sb.AppendLine("未能解析景点详情，请直接访问携程网站查询。");
            }

            sb.AppendLine();
            sb.AppendLine(GenerateAttractionGuide(city, keyword));
            return sb.ToString();
        }

        private static string GenerateAttractionGuide(string city, string? keyword)
        {
            var searchTerm = string.IsNullOrEmpty(keyword) ? city : $"{city} {keyword}";
            return $"🔗 [在携程查看 {searchTerm} 景点门票](https://you.ctrip.com/sight/{HttpUtility.UrlEncode(city)}.html)";
        }

        #endregion

        #region 旅游产品搜索

        /// <summary>
        /// 搜索携程旅游产品（跟团游、自由行等）
        /// </summary>
        /// <param name="destination">目的地</param>
        /// <param name="keyword">关键词（可选）</param>
        /// <returns>格式化的旅游产品搜索结果</returns>
        public async Task<string> SearchTour(string destination, string? keyword = null)
        {
            try
            {
                _logger.LogInformation("搜索携程旅游产品: 目的地={Destination}, 关键词={Keyword}", destination, keyword);

                var client = CreateHttpClient();
                var searchQuery = string.IsNullOrEmpty(keyword) ? $"{destination}旅游" : $"{destination} {keyword}";

                // 使用 Jina Reader API
                var jinaApiKey = Environment.GetEnvironmentVariable("JinaAiApi");
                if (!string.IsNullOrEmpty(jinaApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jinaApiKey}");
                    client.DefaultRequestHeaders.Add("X-Respond-With", "markdown");

                    var ctripUrl = $"https://vacations.ctrip.com/search/s1/{HttpUtility.UrlEncode(destination)}";
                    var jinaUrl = $"https://r.jina.ai/{ctripUrl}";

                    var response = await client.GetAsync(jinaUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        return ExtractTourInfo(content, destination, keyword);
                    }
                }

                return GenerateTourGuide(destination, keyword);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索旅游产品失败");
                return $"搜索旅游产品时发生错误: {ex.Message}\n\n" + GenerateTourGuide(destination, keyword);
            }
        }

        private string ExtractTourInfo(string markdownContent, string destination, string? keyword)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 🌴 {destination} 旅游产品");
            if (!string.IsNullOrEmpty(keyword))
            {
                sb.AppendLine($"🔍 搜索关键词: {keyword}");
            }
            sb.AppendLine();

            var lines = markdownContent.Split('\n');
            int tourCount = 0;

            foreach (var line in lines)
            {
                if (tourCount >= 10) break;

                // 提取旅游产品信息
                if (line.Contains("游") || line.Contains("天") || line.Contains("¥") ||
                    line.Contains("出发") || line.Contains("跟团") || line.Contains("自由行"))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length > 5)
                    {
                        sb.AppendLine(trimmed);
                        if (line.Contains("¥") || line.Contains("天"))
                        {
                            tourCount++;
                        }
                    }
                }
            }

            if (tourCount == 0)
            {
                sb.AppendLine("未能解析旅游产品详情，请直接访问携程网站查询。");
            }

            sb.AppendLine();
            sb.AppendLine(GenerateTourGuide(destination, keyword));
            return sb.ToString();
        }

        private static string GenerateTourGuide(string destination, string? keyword)
        {
            return $"🔗 [在携程查看 {destination} 旅游产品](https://vacations.ctrip.com/search/s1/{HttpUtility.UrlEncode(destination)})";
        }

        #endregion

        #region Helper Methods

        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            return client;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }

        #endregion
    }
}
