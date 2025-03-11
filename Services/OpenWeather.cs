using ChatBot.Models;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Bibliography;
using static ChatBot.Web.Services.WeatherResponse;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// 提供与OpenWeather API交互的服务。
    /// </summary>
    public class OpenWeather
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// 初始化OpenWeather服务。
        /// </summary>
        /// <param name="httpClientFactory">HTTP客户端工厂实例。</param>
        public OpenWeather(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
        }

        /// <summary>
        /// 根据城市名称获取地理编码响应。
        /// </summary>
        /// <param name="city">城市名称。</param>
        /// <returns>返回地理编码响应的列表。</returns>
        public async Task<List<GeocodingResponse>> GetGeocodingAsync(string city)
        {
            var apiKey = Environment.GetEnvironmentVariable("OpenWeatherKey");

            var url = $"https://cdsjf.xyz/openweathermap/geo/1.0/direct?" +
                      $"appid={apiKey}&" +
                      $"q={Uri.EscapeDataString(city)}&" +
                      $"limit=5";

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetStringAsync(url);
            var result = JsonSerializer.Deserialize<List<GeocodingResponse>>(response, _jsonOptions);
            return result ?? new List<GeocodingResponse>();
        }

        /// <summary>
        /// 获取指定城市的天气信息。
        /// </summary>
        /// <param name="city">城市名称。</param>
        /// <returns>返回天气响应。</returns>
        public async Task<string?> GetWeatherAsync(string city)
        {
            var geocodingResponses = await GetGeocodingAsync(city);
            var response = geocodingResponses.FirstOrDefault();
            if (response == null)
            {
              return  await Task.FromResult(string.Empty);
            }

            var apiKey = Environment.GetEnvironmentVariable("OpenWeatherKey");

            var url = $"https://cdsjf.xyz/openweathermap/data/3.0/onecall?" +
                      $"appid={apiKey}&" +
                      $"lat={response.lat}&" +
                      $"lon={response.lon}&" +
                      $"exclude=minutely,hourly,alerts&" +
                      $"lang=zh&" +
                      $"units=metric";

            var client = _httpClientFactory.CreateClient();
            var weatherResponse = await client.GetStringAsync(url);
            var weatherResult = JsonSerializer.Deserialize<WeatherResponse>(weatherResponse, _jsonOptions);

           return await Task.FromResult(weatherResult?.GetWeatherInfo()) ;
        }
    }

    /// <summary>
    /// 表示地理编码的响应数据。
    /// </summary>
    public class GeocodingResponse
    {
        /// <summary>
        /// 城市名称。
        /// </summary>
        public string name { get; set; } = string.Empty;

        /// <summary>
        /// 本地名称。
        /// </summary>
        public GeocodingResponseLocalnames local_names { get; set; } = new GeocodingResponseLocalnames();

        /// <summary>
        /// 纬度。
        /// </summary>
        public double lat { get; set; }

        /// <summary>
        /// 经度。
        /// </summary>
        public double lon { get; set; }

        /// <summary>
        /// 国家代码。
        /// </summary>
        public string country { get; set; } = string.Empty;

        /// <summary>
        /// 州或省份。
        /// </summary>
        public string state { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示地理编码响应中的本地名称。
    /// </summary>
    public class GeocodingResponseLocalnames
    {
        /// <summary>
        /// 中文名称。
        /// </summary>
        public string zh { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示天气响应的数据结构。
    /// </summary>
    public class WeatherResponse
    {
        /// <summary>
        /// 纬度。
        /// </summary>
        public double lat { get; set; }

        /// <summary>
        /// 经度。
        /// </summary>
        public double lon { get; set; }

        /// <summary>
        /// 时区名称。
        /// </summary>
        public string timezone { get; set; } = string.Empty;
        
        /// <summary>
        /// 时区偏移量（秒）。
        /// </summary>
        public int timezone_offset { get; set; }
        
        /// <summary>
        /// 当前天气信息。
        /// </summary>
        public Current current { get; set; } = new Current();
        
        /// <summary>
        /// 每日天气信息列表。
        /// </summary>
        public List<Daily> daily { get; set; } = new List<Daily>();
        
        /// <summary>
        /// 获取天气信息的Markdown格式字符串。
        /// </summary>
        /// <returns>天气信息的Markdown格式字符串。</returns>
        public string GetWeatherInfo()
        {
            var culture = new System.Globalization.CultureInfo("zh-CN");
            var builder = new StringBuilder();
            builder.AppendLine($"# 天气预报\n");
            builder.AppendLine($"**时区**: {timezone}\n");
            builder.AppendLine($"**当前温度**: {Math.Round(current.temp, 0)}°C, **体感温度**: {Math.Round(current.feels_like, 0)}°C");
            builder.AppendLine($"**气压**: {current.pressure} hPa");
            builder.AppendLine($"**湿度**: {current.humidity}%");
            builder.AppendLine($"**风速**: {current.wind_speed} m/s, **风向**: {GetWindDirection(current.wind_deg)}\n");
            builder.AppendLine($"**云量**: {current.clouds}%");
            builder.AppendLine($"**能见度**: {current.visibility}m");
            builder.AppendLine($"**紫外线强度**: {current.uvi}");
            
            var cd = UnixTimeStampToDateTime(current.sunset+this.timezone_offset).ToString("HH:mm");
            if (current.weather.Any())
            {
                
                builder.AppendLine($"**天气**: {current.weather.First().description} {WeatherIcons[current.weather.First().icon]}\n");
            }

            if (daily.Any())
            {
                builder.AppendLine("## 每日天气预报\n");
                foreach (var day in daily)
                {
                    var icon = day.weather.FirstOrDefault()?.icon;
                    var iconMarkdown = icon != null ? WeatherIcons[icon] : string.Empty;
                    builder.AppendLine($"- **日期**: {UnixTimeStampToDateTime(day.dt + timezone_offset).ToString("M月dd日")} {UnixTimeStampToDateTime(day.dt + timezone_offset).ToString("dddd", culture)}");
                    builder.AppendLine($"  - **日出**: {UnixTimeStampToDateTime(day.sunrise+timezone_offset).ToString("HH:mm")}");
                    builder.AppendLine($"  - **日落**: {UnixTimeStampToDateTime(day.sunset + timezone_offset).ToString("HH:mm")}");
                    builder.AppendLine($"  - **最高温度**: {Math.Round(day.temp.max, 0)}°C");
                    builder.AppendLine($"  - **最低温度**: {Math.Round(day.temp.min, 0)}°C");
                    builder.AppendLine($"  - **气压**: {day.pressure} hPa");
                    builder.AppendLine($"  - **湿度**: {day.humidity}%");
                    builder.AppendLine($"  - **风速**: {day.wind_speed} m/s, **风向**: {GetWindDirection(day.wind_deg)}");
                    builder.AppendLine($"  - **云量**: {day.clouds}%");
                    builder.AppendLine($"  - **紫外线强度**: {day.uvi}");
                    builder.AppendLine($"  - **天气**: {day.weather.FirstOrDefault()?.description} {iconMarkdown}\n");
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 根据风向角度获取风向描述。
        /// </summary>
        /// <param name="windDeg">风向角度。</param>
        /// <returns>风向描述。</returns>
        private string GetWindDirection(int windDeg)
        {
            string[] directions = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
            int index = (windDeg + 22) / 45 % 8;
            return directions[index];
        }
        
        /// <summary>
        /// 将Unix时间戳转换为DateTime。
        /// </summary>
        /// <param name="unixTimeStamp">Unix时间戳。</param>
        /// <returns>对应的DateTime。</returns>
        private DateTime UnixTimeStampToDateTime(int unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            return DateTimeOffset.FromUnixTimeSeconds(unixTimeStamp).DateTime;
        }

        /// <summary>
        /// 当前天气详细信息。
        /// </summary>
        public class Current
        {
            /// <summary>
            /// 数据时间，以Unix时间戳表示。
            /// </summary>
            public int dt { get; set; }

            /// <summary>
            /// 日出时间，以Unix时间戳表示。
            /// </summary>
            public int sunrise { get; set; }

            /// <summary>
            /// 日落时间，以Unix时间戳表示。
            /// </summary>
            public int sunset { get; set; }

            /// <summary>
            /// 当前温度。
            /// </summary>
            public double temp { get; set; }

            /// <summary>
            /// 体感温度。
            /// </summary>
            public double feels_like { get; set; }

            /// <summary>
            /// 气压。
            /// </summary>
            public int pressure { get; set; }

            /// <summary>
            /// 湿度。
            /// </summary>
            public int humidity { get; set; }

            /// <summary>
            /// 露点温度。
            /// </summary>
            public double dew_point { get; set; }

            /// <summary>
            /// 紫外线强度。
            /// </summary>
            public double uvi { get; set; }

            /// <summary>
            /// 云量百分比。
            /// </summary>
            public int clouds { get; set; }

            /// <summary>
            /// 能见度。
            /// </summary>
            public int visibility { get; set; }

            /// <summary>
            /// 风速。
            /// </summary>
            public double wind_speed { get; set; }

            /// <summary>
            /// 风向角度。
            /// </summary>
            public int wind_deg { get; set; }

            /// <summary>
            /// 天气描述列表。
            /// </summary>
            public List<Weather> weather { get; set; } = new List<Weather>();
        }

        /// <summary>
        /// 每日天气详细信息。
        /// </summary>
        public class Daily
        {
            /// <summary>
            /// 数据时间，以Unix时间戳表示。
            /// </summary>
            public int dt { get; set; }

            /// <summary>
            /// 日出时间，以Unix时间戳表示。
            /// </summary>
            public int sunrise { get; set; }

            /// <summary>
            /// 日落时间，以Unix时间戳表示。
            /// </summary>
            public int sunset { get; set; }

            /// <summary>
            /// 温度信息。
            /// </summary>
            public Temp temp { get; set; } = new Temp();

            /// <summary>
            /// 体感温度信息。
            /// </summary>
            public FeelsLike feels_like { get; set; } = new FeelsLike();

            /// <summary>
            /// 气压。
            /// </summary>
            public int pressure { get; set; }

            /// <summary>
            /// 湿度。
            /// </summary>
            public int humidity { get; set; }

            /// <summary>
            /// 露点温度。
            /// </summary>
            public double dew_point { get; set; }

            /// <summary>
            /// 风速。
            /// </summary>
            public double wind_speed { get; set; }

            /// <summary>
            /// 风向角度。
            /// </summary>
            public int wind_deg { get; set; }

            /// <summary>
            /// 天气描述列表。
            /// </summary>
            public List<Weather> weather { get; set; } = new List<Weather>();

            /// <summary>
            /// 云量百分比。
            /// </summary>
            public int clouds { get; set; }

            /// <summary>
            /// 降水概率。
            /// </summary>
            public double pop { get; set; }

            /// <summary>
            /// 紫外线强度。
            /// </summary>
            public double uvi { get; set; }
        }

        /// <summary>
        /// 温度信息。
        /// </summary>
        public class Temp
        {
            /// <summary>
            /// 白天天气温度。
            /// </summary>
            public double day { get; set; }

            /// <summary>
            /// 最低温度。
            /// </summary>
            public double min { get; set; }

            /// <summary>
            /// 最高温度。
            /// </summary>
            public double max { get; set; }

            /// <summary>
            /// 夜间温度。
            /// </summary>
            public double night { get; set; }

            /// <summary>
            /// 傍晚温度。
            /// </summary>
            public double eve { get; set; }

            /// <summary>
            /// 早晨温度。
            /// </summary>
            public double morn { get; set; }
        }

        /// <summary>
        /// 体感温度信息。
        /// </summary>
        public class FeelsLike
        {
            /// <summary>
            /// 白天天气体感温度。
            /// </summary>
            public double day { get; set; }

            /// <summary>
            /// 夜间天气体感温度。
            /// </summary>
            public double night { get; set; }

            /// <summary>
            /// 傍晚天气体感温度。
            /// </summary>
            public double eve { get; set; }

            /// <summary>
            /// 早晨天气体感温度。
            /// </summary>
            public double morn { get; set; }
        }

        /// <summary>
        /// 天气描述信息。
        /// </summary>
        public class Weather
        {
            /// <summary>
            /// 天气情况的ID。
            /// </summary>
            public int id { get; set; }

            /// <summary>
            /// 天气情况的主要描述。
            /// </summary>
            public string main { get; set; } = string.Empty;

            /// <summary>
            /// 天气情况的详细描述。
            /// </summary>
            public string description { get; set; } = string.Empty;

            /// <summary>
            /// 天气图标的代码。
            /// </summary>
            public string icon { get; set; } = string.Empty;
        }
        public static Dictionary<string, string> WeatherIcons = new Dictionary<string, string>
        {
            { "01d", "☀️" },
            { "01n", "🌙" },
            { "02d", "⛅️" },
            { "02n", "⛅️" },
            { "03d", "☁️" },
            { "03n", "☁️" },
            { "04d", "☁️" },
            { "04n", "☁️" },
            { "09d", "🌧" },
            { "09n", "🌧" },
            { "10d", "🌦" },
            { "10n", "🌦" },
            { "11d", "🌩" },
            { "11n", "🌩" },
            { "13d", "❄️" },
            { "13n", "❄️" },
            { "50d", "🌫" },
            { "50n", "🌫" }
        };
    }
}
