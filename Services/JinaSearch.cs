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

namespace ChatBot.Web.Services
{
    public class JinaSearch
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly JinaSearchResult _result;
        private readonly Dictionary<string, string> _nameToCode ;
        private readonly Dictionary<string, string> _codeToName;

        public JinaSearchResult Result => _result;

        public async Task<JinaSearch> InitializeAsync()
        {
            string stationData = await GetStationDataAsync();
            ParseStationData(stationData, _nameToCode, _codeToName);
            return this;
        }

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
            _nameToCode = new Dictionary<string, string>();
            _codeToName = new Dictionary<string, string>();

            // Call the async initialization method
            InitializeAsync().GetAwaiter().GetResult();
        }


        public async Task<string?> Search(string query, int searchCount = 5, bool isNoCache = false, bool isdirect = true)
        {
            _result.Data.Clear();
            var google = await Search(query, "", searchCount, isNoCache, isdirect);
            //var bing = await Search(query, "bing.com ", searchCount, isNoCache, isdirect);
            var badu = await Search(query, "baidu.com ", searchCount, isNoCache, isdirect);
            //var sg = await Search(query, "sogou.com ", searchCount, isNoCache, isdirect);
            //var tencent = await Search(query, "qq.com ", searchCount, isNoCache, isdirect);
            //var zhihu = await Search(query, "zhihu.com ", searchCount, isNoCache, isdirect);
            //var weibo = await Search(query, "weibo.com ", searchCount, isNoCache, isdirect);
            var ctrip = await Search(query, "ctrip.com ", searchCount, isNoCache, isdirect);
            var dzdp = await Search(query, "dianping.com ", searchCount, isNoCache, isdirect);
            //var douban = await Search(query, "douban.com ", searchCount, isNoCache, isdirect);
            //var reddit = await Search(query, "reddit.com ", searchCount, isNoCache, isdirect);
            var Wikipedia = await Search(query, "wikipedia.org ", searchCount, isNoCache, isdirect);




            if (google?.Data != null)
            {
                _result.Data.AddRange(google.Data);
            }
            //if (bing?.Data != null)
            //{
            //    _result.Data.AddRange(bing.Data);
            //}
            if (badu?.Data != null)
            {
                _result.Data.AddRange(badu.Data);
            }
            //if (tencent?.Data != null)
            //{
            //    _result.Data.AddRange(tencent.Data);
            //}
            //if (zhihu?.Data != null)
            //{
            //    _result.Data.AddRange(zhihu.Data);
            //}
            //if (weibo?.Data != null)
            //{
            //    _result.Data.AddRange(weibo.Data);
            //}
            if (ctrip?.Data != null)
            {
                _result.Data.AddRange(ctrip.Data);
            }
            //if (douban?.Data != null)
            //{
            //    _result.Data.AddRange(douban.Data);
            //}
            //if (reddit?.Data != null)
            //{
            //    _result.Data.AddRange(reddit.Data);
            //}

            //if (sg?.Data != null)
            //{
            //    _result.Data.AddRange(sg.Data);
            //}

            if (Wikipedia?.Data != null)
            {
                _result.Data.AddRange(Wikipedia.Data);
            }
            if (dzdp?.Data != null)
            {
                _result.Data.AddRange(dzdp.Data);
            }
            await GetInfoFromGemini(query);
            await Rerank(query);
            var str = await MergeInfo();
            return await Task.FromResult(str);
        }

        public async Task<JinaSearchResult?> Search(string query, string site, int searchCount = 5, bool isNoCache = false, bool isdirect = true)
        {
            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://s.jina.ai";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            client.DefaultRequestHeaders.Add("X-Respond-With", "no-content");
            //client.DefaultRequestHeaders.Add("X-Locale", "zh-CN");
            //client.DefaultRequestHeaders.Add("X-Proxy", "auto");
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

                //loc = new string[] { "visas lang:zh", "visas lang:en" },
                //site = new string[] { "goggles site:brave.com" }
            };

            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    response.EnsureSuccessStatusCode();
                    string rsteing = await response.Content.ReadAsStringAsync();
                    var jinaSearchResult = JsonSerializer.Deserialize<JinaSearchResult>(rsteing, _jsonOptions);

                    if (jinaSearchResult != null)
                    {
                        foreach (var item in jinaSearchResult.Data)
                        {
                            item.Url = Uri.UnescapeDataString(item.Url);
                        }

                        for (int i = jinaSearchResult.Data.Count - 1; i >= 0; i--)
                        {


                            for (int k = i - 1; k >= 0; k--)
                            {
                                if (jinaSearchResult.Data[i].Url.Equals(jinaSearchResult.Data[k].Url, StringComparison.Ordinal))
                                {
                                    jinaSearchResult.Data.RemoveAt(i);
                                    break;
                                }
                            }

                        }
                        for (int i = jinaSearchResult.Data.Count - 1; i >= 0; i--)
                        {

                            bool isRemove = false;
                            for (int k = 0; k < _result.Data.Count; k++)
                            {
                                if (_result.Data[k].Url.Equals(jinaSearchResult.Data[i].Url, StringComparison.Ordinal))
                                {
                                    isRemove = true;
                                    break;
                                }
                            }
                            if (isRemove)
                            {
                                jinaSearchResult.Data.RemoveAt(i);
                            }
                        }
                        if (jinaSearchResult.Data.Count == 0)
                        {
                            return await Task.FromResult<JinaSearchResult?>(jinaSearchResult);
                        }

                        await Reader(jinaSearchResult, isNoCache, isdirect);

                        return await Task.FromResult<JinaSearchResult?>(jinaSearchResult);
                    }
                    else
                    {
                        return await Task.FromResult<JinaSearchResult?>(null);
                    }

                }
                else
                {
                    return await Task.FromResult<JinaSearchResult?>(null);
                }
            }
        }
        public async Task Reader(JinaSearchResult jinaSearchResult, bool isNoCache = false, bool isdirect = true)
        {
            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://r.jina.ai";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("X-Retain-Images", "none");
            //client.DefaultRequestHeaders.Add("X-Locale", "zh-CN");
            //client.DefaultRequestHeaders.Add("X-Proxy", "auto");
            if (isdirect)
            {
                client.DefaultRequestHeaders.Add("X-Engine", "direct");
            }
            if (isNoCache)
            {
                client.DefaultRequestHeaders.Add("X-No-Cache", "true");
            }

            var tasks = jinaSearchResult.Data.Select(async item =>
            {
                var requestContent = new
                {
                    url = item.Url,
                };

                using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        response.EnsureSuccessStatusCode();
                        string rsteing = await response.Content.ReadAsStringAsync();
                        var jinaSearchResult1 = JsonSerializer.Deserialize<JinaReaderResult>(rsteing, _jsonOptions);

                        if (jinaSearchResult1 != null)
                        {
                            item.Content = jinaSearchResult1.Data.Content;
                        }
                    }
                }
            });

            await Task.WhenAll(tasks);
        }
        public async Task Rerank1(string query, int rerankCount = 3)
        {
            for (int i = _result.Data.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(_result.Data[i].Content))
                {
                    _result.Data.RemoveAt(i);
                }
                else
                {
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (_result.Data[i].Content == _result.Data[k].Content || _result.Data[i].Url.Equals(_result.Data[k].Url, StringComparison.Ordinal))
                        {
                            _result.Data.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://api.jina.ai/v1/rerank";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            var tasks = _result.Data.Select(async item =>

            {

                if (string.IsNullOrWhiteSpace(item.Content)) return;

                var requestContent = new
                {
                    model = "jina-reranker-v2-base-multilingual",
                    query = query,
                    top_n = rerankCount,
                    documents = (new TextSplitter()).Split(item.Content)

                };
                item.Content = string.Empty;
                item.Description = string.Empty;
                try
                {
                    using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                    }, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            response.EnsureSuccessStatusCode();
                            string rsteing = await response.Content.ReadAsStringAsync();
                            var jinaRerankResult = JsonSerializer.Deserialize<JinaRerankResult>(rsteing, _jsonOptions);
                            if (jinaRerankResult != null)
                            {
                                StringBuilder sb = new StringBuilder();
                                for (int i = 0; i < System.Math.Min(jinaRerankResult.Results.Count, rerankCount); i++)
                                {
                                    sb.AppendLine(jinaRerankResult.Results[i].Document.Text);
                                }
                                item.Content = sb.ToString();
                            }

                        }

                    }
                }


                catch (Exception ex)
                {

                }


            });

            await Task.WhenAll(tasks);
            _result.Data.RemoveAll(x => string.IsNullOrWhiteSpace(x.Content));

        }
        public async Task Rerank(string query, int rerankCount = 10)
        {
            for (int i = _result.Data.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(_result.Data[i].Content))
                {
                    _result.Data.RemoveAt(i);
                }
                else
                {
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (_result.Data[i].Content == _result.Data[k].Content || _result.Data[i].Url.Equals(_result.Data[k].Url, StringComparison.Ordinal))
                        {
                            _result.Data.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://api.jina.ai/v1/rerank";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");






            var requestContent = new
            {
                model = "jina-reranker-v2-base-multilingual",
                query = query,
                top_n = rerankCount,
                documents = _result.Data.Select(x => x.Content).ToList()

            };

            try
            {
                using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        response.EnsureSuccessStatusCode();
                        string rsteing = await response.Content.ReadAsStringAsync();
                        var jinaRerankResult = JsonSerializer.Deserialize<JinaRerankResult>(rsteing, _jsonOptions);
                        if (jinaRerankResult != null)
                        {

                            for (int i = _result.Data.Count - 1; i >= 0; i--)
                            {
                                bool flag = false;
                                for (int k = 0; k < System.Math.Min(jinaRerankResult.Results.Count, rerankCount); k++)
                                {
                                    if (i == jinaRerankResult.Results[k].Index)
                                    {
                                        flag = true;
                                    }
                                }
                                if (!flag)
                                {
                                    _result.Data.RemoveAt(i);
                                }
                            }


                        }

                    }

                }
            }


            catch (Exception ex)
            {

            }


        }
        public async Task GetInfoFromOpenai(string query)
        {

            for (int i = _result.Data.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(_result.Data[i].Content))
                {
                    _result.Data.RemoveAt(i);
                }
                else
                {
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (_result.Data[i].Content == _result.Data[k].Content || _result.Data[i].Url.Equals(_result.Data[k].Url, StringComparison.Ordinal))
                        {
                            _result.Data.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            var apiKey = Environment.GetEnvironmentVariable("OpenAiKey");
            var apiEndpoint = $"https://cdsjf.xyz/openai/v1/chat/completions";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");



            var tasks = _result.Data.Select(async item =>

            {

                if (string.IsNullOrWhiteSpace(item.Content)) return;

                var contents = new List<object>();
                contents.Add(new { role = "system", content = "你是一个优秀的资料整理专家。" });
                contents.Add(new
                {
                    role = "user",
                    content = ((new StringBuilder()).Append("在<info></info>中摘要和")
                     .Append(query)
                    .Append("相关的信息，如果没有摘要信息则回答:[NOT]")
                    .Append('\n')
                    .Append('\n')
                    .Append("<info>")
                     .Append('\n')
                     .Append(item.Content)
                    .Append('\n')
                    .Append("</info>")

                    .ToString())
                });

                var requestContent = new
                {
                    model = "gpt-4o-mini",
                    messages = contents,
                    temperature = 0.1,

                };
                item.Content = string.Empty;
                item.Description = string.Empty;
                try
                {
                    using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                    }, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            response.EnsureSuccessStatusCode();
                            string rsteing = await response.Content.ReadAsStringAsync();


                            var jinaRerankResult = JsonSerializer.Deserialize<OpenAIResponse>(rsteing, _jsonOptions);
                            if (jinaRerankResult != null)
                            {
                                var content = jinaRerankResult?.choices?.FirstOrDefault()?.message?.content;


                                if (!string.IsNullOrWhiteSpace(content) && !content.Contains("[NOT]"))
                                {
                                    item.Content = content ?? string.Empty;
                                }
                            }

                        }

                    }
                }


                catch (Exception ex)
                {

                }


            });

            await Task.WhenAll(tasks);
            _result.Data.RemoveAll(x => string.IsNullOrWhiteSpace(x.Content));

        }
        public async Task GetInfoFromGemini(string query)
        {

            for (int i = _result.Data.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(_result.Data[i].Content))
                {
                    _result.Data.RemoveAt(i);
                }
                else
                {
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (_result.Data[i].Content == _result.Data[k].Content || _result.Data[i].Url.Equals(_result.Data[k].Url, StringComparison.Ordinal))
                        {
                            _result.Data.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            var apiKey = Environment.GetEnvironmentVariable("GeminiKey");
            var apiEndpoint = $"https://cdsjf.xyz/gemini/v1beta/openai/chat/completions";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");



            var tasks = _result.Data.Select(async item =>

            {

                if (string.IsNullOrWhiteSpace(item.Content)) return;

                var contents = new List<object>();
                contents.Add(new { role = "system", content = "你是一个优秀的资料整理专家，能够准确提取与特定查询相关的信息。" });
                contents.Add(new
                {
                    role = "user",
                    content = new StringBuilder()
                    .Append("请从以下<info></info>中提取与查询\"")
                    .Append(query)
                    .Append("\"相关的详细准确信息。")
                    .Append("。如果内容与查询无关，请回答: [NOT]")
                    .Append('\n')
                    .Append('\n')
                    .Append("<info>")
                    .Append('\n')
                    .Append(item.Content)
                    .Append('\n')
                    .Append("</info>")
                    .ToString()
                });

                var requestContent = new
                {
                    model = "gemini-2.0-flash-lite",
                    messages = contents,
                    temperature = 0.1,

                };
                item.Content = string.Empty;
                item.Description = string.Empty;
                try
                {
                    using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                    }, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            response.EnsureSuccessStatusCode();
                            string rsteing = await response.Content.ReadAsStringAsync();


                            var jinaRerankResult = JsonSerializer.Deserialize<OpenAIResponse>(rsteing, _jsonOptions);
                            if (jinaRerankResult != null)
                            {
                                var content = jinaRerankResult?.choices?.FirstOrDefault()?.message?.content;
                                if (!string.IsNullOrWhiteSpace(content) && !content.Contains("[NOT]"))
                                {
                                    item.Content = content ?? string.Empty;
                                }
                            }

                        }

                    }
                }


                catch (Exception ex)
                {

                }


            });

            await Task.WhenAll(tasks);
            _result.Data.RemoveAll(x => string.IsNullOrWhiteSpace(x.Content));

        }
        public async Task<string> MergeInfo()
        {

            var apiKey = Environment.GetEnvironmentVariable("GeminiKey");
            var apiEndpoint = $"https://cdsjf.xyz/gemini/v1beta/openai/chat/completions";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            StringBuilder sb = new StringBuilder();
            foreach (var item in _result.Data)
            {
                if (string.IsNullOrWhiteSpace(item.Content)) continue;
                sb.Append("Url:");
                sb.Append(item.Url);
                sb.AppendLine("Title:");
                sb.Append(item.Title);
                sb.AppendLine("Content：");
                sb.AppendLine(item.Content);
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine();

            }

            var contents = new List<object>();
            contents.Add(new { role = "system", content = "你是一个优秀的资料整合专家，善于将多个来源的信息融合为连贯、详细且易于理解的文本。请确保保留所有重要信息，并正确引用来源。" });
            contents.Add(new
            {
                role = "user",
                content = ((new StringBuilder()).AppendLine("1、整合<info></info>中资料,保持尽可能多的原始信息和细节。")
                .AppendLine("2、在描述中使用以空格符作为间隔的引用标记，引用信息来源（格式：[1] [3] [11]）,并且在内容末尾提供完整引用来源列表，格式为：\n\n [1]: https://example.com \n\n [2]: https://example.com")
                .AppendLine("3、组织信息时，请按主题或时间顺序结构化内容，避免简单堆砌。")
                .AppendLine("4、如有相互矛盾的信息，请一并呈现并标明来源。")
                .Append('\n')
                .Append('\n')
                .Append("<info>")
                 .Append('\n')
                 .Append(sb.ToString())
                .Append('\n')
                .Append("</info>")

                .ToString())
            });

            var requestContent = new
            {
                model = "gemini-2.0-flash-lite",
                messages = contents,
                temperature = 0.1,

            };

            try
            {
                using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        response.EnsureSuccessStatusCode();
                        string rsteing = await response.Content.ReadAsStringAsync();


                        var jinaRerankResult = JsonSerializer.Deserialize<OpenAIResponse>(rsteing, _jsonOptions);
                        if (jinaRerankResult != null)
                        {
                            var content = jinaRerankResult?.choices?.FirstOrDefault()?.message?.content;
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                return await Task.FromResult(content);
                            }
                        }

                    }

                }
            }


            catch (Exception ex)
            {

            }

            return await Task.FromResult(string.Empty);
        }

        //    public async Task<string?> SearchTrainTicket(string Startingplace, string Arrivalplace, string date)
        //    {

        //        var  Startingplacecode = _nameToCode.GetValueOrDefault(Startingplace);
        //        var Arrivalplacecode = _nameToCode.GetValueOrDefault(Arrivalplace);

        //        if( string.IsNullOrEmpty( Startingplacecode)|| string.IsNullOrEmpty( Arrivalplacecode ))
        //        {
        //            return await Task.FromResult(string.Empty);
        //        }

        //        var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
        //        var apiEndpoint = $"https://kyfw.12306.cn/otn/leftTicket/queryR";

        //        var client = _httpClientFactory.CreateClient();
        //        client.Timeout = TimeSpan.FromMinutes(30);
        //        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        //        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
        //        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        //        client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        //        client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        //        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        //        //client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        //        //client.DefaultRequestHeaders.Add("Accept", "application/json");
        //        //client.DefaultRequestHeaders.Add("X-Retain-Images", "none");
        //        //client.DefaultRequestHeaders.Add("X-No-Cache", "true");

        //        string uri = $"https://kyfw.12306.cn/otn/leftTicket/queryR?leftTicketDTO.train_date={date}&leftTicketDTO.from_station={Startingplacecode}&leftTicketDTO.to_station={Arrivalplacecode}&purpose_codes=ADULT";
        //        string loguri = $"https://kyfw.12306.cn/otn/leftTicket/log?leftTicketDTO.train_date={date}&leftTicketDTO.from_station={Startingplacecode}&leftTicketDTO.to_station={Arrivalplacecode}&purpose_codes=ADULT";
        //        var requestContent = new
        //        {
        //            leftTicketDTO= new
        //            {

        //                train_date = date,
        //                from_station = Startingplacecode,
        //                to_station = Arrivalplacecode,
        //                purpose_codes = "ADULT"
        //            }




        //        };

        //        using (HttpClient client2 = new HttpClient())
        //        {
        //            string url = uri;
        //            var str1= await client.GetStringAsync(loguri);
        //        }
        //        using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri)
        //        //{
        //        //    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
        //        //}
        //        ))
        //        {
        //            if (response.StatusCode == System.Net.HttpStatusCode.OK)
        //            {
        //                response.EnsureSuccessStatusCode();
        //                string rsteing = await response.Content.ReadAsStringAsync();


        //                //return await Task.FromResult(rsteing);
        //            }
        //        }

        //        return await Task.FromResult(string.Empty);

        //}

        public async Task<string?> SearchTrainTicket(string Startingplace, string Arrivalplace, string date)
        {
            var Startingplacecode = _nameToCode.GetValueOrDefault(Startingplace);
            var Arrivalplacecode = _nameToCode.GetValueOrDefault(Arrivalplace);

            if (string.IsNullOrEmpty(Startingplacecode) || string.IsNullOrEmpty(Arrivalplacecode))
            {
                return await Task.FromResult("未找到车站代码");
            }

            try
            {
                // 创建一个HttpClientHandler以自定义SSL/TLS处理
                var handler = new HttpClientHandler
                {
                    // 接受所有证书（仅用于开发，生产环境应使用正确的证书验证）
                    ServerCertificateCustomValidationCallback =
                        (message, cert, chain, sslPolicyErrors) => true,
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                };

                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(1);

                    // 添加浏览器常见请求头
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
                    client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                    client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
                    client.DefaultRequestHeaders.Add("Connection", "keep-alive");
                    client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
                    client.DefaultRequestHeaders.Referrer = new Uri("https://kyfw.12306.cn/");

                    // 日志URI
                    string loguri = $"https://kyfw.12306.cn/otn/leftTicket/log?leftTicketDTO.train_date={date}&leftTicketDTO.from_station={Startingplacecode}&leftTicketDTO.to_station={Arrivalplacecode}&purpose_codes=ADULT";

                    // 先请求日志接口
                    await client.GetStringAsync(loguri);

                    // 主查询URI
                    string uri = $"https://kyfw.12306.cn/otn/leftTicket/query?leftTicketDTO.train_date={date}&leftTicketDTO.from_station={Startingplacecode}&leftTicketDTO.to_station={Arrivalplacecode}&purpose_codes=ADULT";

                    var response = await client.GetAsync(uri);

                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync();

                        // 解析JSON响应
                        var trainData = JsonSerializer.Deserialize<Train>(content, _jsonOptions);

                        // 处理数据并返回格式化结果
                        if (trainData != null && trainData.data.result.Count > 0)
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
            }
            catch (Exception ex)
            {
                return $"发生错误：{ex.Message}";
            }
        }

        // 格式化列车数据为可读形式
        private string FormatTrainData(Train trainData, Dictionary<string, string> codeToName)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("列车查询结果：");

            foreach (var item in trainData.data.result)
            {
                // 解析列车数据字符串，具体格式需要根据实际返回调整
                string[] segments = item.Split('|');
                if (segments.Length > 30)
                {
                    string trainCode = segments[3]; // 车次
                    string fromStation = codeToName.GetValueOrDefault(segments[4], segments[4]); // 始发站
                    string toStation = codeToName.GetValueOrDefault(segments[5], segments[5]); // 终点站
                    string fromStation1 = codeToName.GetValueOrDefault(segments[6], segments[6]); // 出发站
                    string toStation1 = codeToName.GetValueOrDefault(segments[7], segments[7]); // 到达站
                    string startTime = segments[8]; // 出发时间
                    string arriveTime = segments[9]; // 到达时间
                    string duration = segments[10]; // 历时

                    // 座位信息（不同车次的索引可能不同）
                    string ywSeat = segments[28]; // 硬卧
                    string yzSeat = segments[29]; // 硬座
                    string rwSeat = segments[23]; // 软卧
                    string tdSeat = segments[32]; // 特等座
                    string swzSeat = segments[25] == "" ? "无" : segments[25]; // 商务座
                    string zyDeat = segments[31]; // 一等座
                    string edSeat = segments[30]; // 二等座

                    sb.AppendLine($"车次: {trainCode}");
                    sb.AppendLine($"始发/终点: {fromStation} -> {toStation}");
                    sb.AppendLine($"出发/到达: {fromStation1} -> {toStation1}");
                    sb.AppendLine($"时间: {startTime} -> {arriveTime} 历时: {duration}");
                    sb.AppendLine($"座位: 商务: {swzSeat}，特等：{tdSeat}, 一等: {zyDeat}, 二等: {edSeat}");
                    sb.AppendLine($"      软卧: {rwSeat},  硬卧: {ywSeat}, 硬座: {yzSeat}");
                    sb.AppendLine("----------------------");
                }
            }

            return sb.ToString();
        }

        static async Task<string> GetStationDataAsync()
        {
            using (HttpClient client = new HttpClient())
            {
                string url = "https://kyfw.12306.cn/otn/resources/js/framework/station_name.js";
                return await client.GetStringAsync(url);
            }
        }

        // 解析车站数据
        static void ParseStationData(string data, Dictionary<string, string> nameToCode, Dictionary<string, string> codeToName)
        {
            // 提取有效部分（去掉开头的"var station_names ="）
            string pattern = @"@([a-z]+)\|([^|]+)\|([A-Z]+)\|";
            MatchCollection matches = Regex.Matches(data, pattern);

            foreach (Match match in matches)
            {
                string name = match.Groups[2].Value;  // 站名
                string code = match.Groups[3].Value;  // 站代码

                if (!nameToCode.ContainsKey(name))
                    nameToCode[name] = code;

                if (!codeToName.ContainsKey(code))
                    codeToName[code] = name;
            }
        }

        public static string getsegstr(string as_str, char as_fgstr, int ai_num)
        {

            int j = 0, i = 0;

            if (string.IsNullOrEmpty(as_str) || ai_num <= 0) return string.Empty;
            ReadOnlySpan<char> strSpan = as_str.AsSpan();

            if (strSpan.IndexOf(as_fgstr) < 0)
            {
                if (ai_num == 1)
                {
                    return as_str;
                }
                else
                {
                    return string.Empty;
                }

            }


            j = strSpan.IndexOf(as_fgstr);
            while (j >= 0)
            {
                i++;
                if (i == ai_num)
                {
                    return strSpan.Slice(0, j).ToString();

                }
                strSpan = strSpan.Slice(j + 1);
                j = strSpan.IndexOf(as_fgstr);

            }
            i++;
            if (i == ai_num)
            {
                return strSpan.ToString();
            }
            return string.Empty;
        }
    }

    public class TextSplitter
{
    private readonly int _maxChunkSize;
    private readonly int _overlap;

    // 构造函数，设置切片大小和重叠部分
    public TextSplitter(int maxChunkSize = 512, int overlap = 50)
    {
        _maxChunkSize = maxChunkSize;
        _overlap = overlap;
    }

    public IEnumerable<string> Split(string text)
    {
        // 1. 首先按段落分割
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" },
            StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new StringBuilder();
        var chunks = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            // 如果段落本身超过最大长度，需要进一步分割
            if (paragraph.Length > _maxChunkSize)
            {
                // 处理当前累积的内容
                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString());
                    currentChunk.Clear();
                }

                // 对长段落进行句子级别的分割
                var sentences = SplitIntoSentences(paragraph);
                var sentenceChunks = ChunkSentences(sentences);
                chunks.AddRange(sentenceChunks);
            }
            else
            {
                // 判断添加这个段落是否会超出最大长度
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

        // 添加最后一个chunk
        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        // 处理重叠部分
        return AddOverlap(chunks);
    }

    private IEnumerable<string> SplitIntoSentences(string text)
    {
        // 使用正则表达式分割句子
        var sentencePattern = @"(?<=[.!?。！？])\s+";
        return Regex.Split(text, sentencePattern)
            .Where(s => !string.IsNullOrWhiteSpace(s));
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

                // 如果单个句子超过最大长度，强制切分
                if (sentence.Length > _maxChunkSize)
                {
                    var words = sentence.Split(' ');
                    foreach (var chunk in ChunkByWords(words))
                    {
                        chunks.Add(chunk);
                    }
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
        var chunks = new List<string>();

        foreach (var word in words)
        {
            if (currentChunk.Length + word.Length + 1 > _maxChunkSize)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
            }

            if (currentChunk.Length > 0)
            {
                currentChunk.Append(" ");
            }
            currentChunk.Append(word);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        return chunks;
    }

    private IEnumerable<string> AddOverlap(List<string> chunks)
    {
        if (_overlap <= 0 || chunks.Count <= 1)
            return chunks;

        var result = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
            {
                // 从前一个chunk的末尾获取重叠内容
                var previousChunk = chunks[i - 1];
                var overlapFromPrevious = previousChunk.Length <= _overlap
                    ? previousChunk
                    : previousChunk.Substring(previousChunk.Length - _overlap);

                result.Add(overlapFromPrevious + chunks[i]);
            }
            else
            {
                result.Add(chunks[i]);
            }
        }

        return result;
    }
}

public class RerankItem
{
    public JinaSearchResultData jinaSearchResultData { get; set; }
    public int beging { get; set; }
    public int end { get; set; }
}

    public class Train
    {
        public Traindata data { get; set; } = new Traindata();

        public class Traindata
        {

            public List<string> result { get; set; } = new List<string>();
        }
    
    
    }
}
