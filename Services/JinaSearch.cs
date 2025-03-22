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

namespace ChatBot.Web.Services
{
    public class JinaSearch
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly JinaSearchResult _result;

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
        }

        //public async Task<JinaSearchResult?> JinaAiSearch(string query, int searchCount = 5, bool isNoCache = false, bool isdirect = true)
        //{
        //    var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
        //    var apiEndpoint = $"https://s.jina.ai";

        //    var client = _httpClientFactory.CreateClient();
        //    client.Timeout = TimeSpan.FromMinutes(30);

        //    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        //    client.DefaultRequestHeaders.Add("Accept", "application/json");
        //    client.DefaultRequestHeaders.Add("X-Retain-Images", "none");
        //    //client.DefaultRequestHeaders.Add("X-Return-Format", "text");
        //    if (isdirect)
        //    {
        //        client.DefaultRequestHeaders.Add("X-Engine", "direct");
        //    }
        //    if (isNoCache)
        //    {
        //        client.DefaultRequestHeaders.Add("X-No-Cache", "true");
        //    }



        //    var requestContent = new

        //    {
        //        q = query,
        //        count = searchCount,
        //        loc = new string[] { "visas lang:zh", "visas lang:en" },
        //        //site = new string[] { "goggles site:brave.com" }
        //    };

        //    using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
        //    {
        //        Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
        //    }, HttpCompletionOption.ResponseHeadersRead))
        //    {
        //        if (response.StatusCode == System.Net.HttpStatusCode.OK)
        //        {
        //            response.EnsureSuccessStatusCode();
        //            string rsteing = await response.Content.ReadAsStringAsync();
        //            var jinaSearchResult = JsonSerializer.Deserialize<JinaSearchResult>(rsteing);

        //            if (jinaSearchResult != null)
        //            {
        //                await JinaAiRerank(query, jinaSearchResult);
        //            }
        //            return await Task.FromResult<JinaSearchResult?>(jinaSearchResult);
        //        }
        //        else
        //        {
        //            return await Task.FromResult<JinaSearchResult?>(null);
        //        }
        //    }
        //}

        public async Task<JinaSearchResult?> JinaAiSearch(string query, int searchCount = 5, bool isNoCache = false, bool isdirect = true)
        {
            _result.Data.Clear();
            var google = await JinaAiSearch(query, "", searchCount, isNoCache, isdirect);
            //var bing = await JinaAiSearch(query, "bing.com ", searchCount, isNoCache, isdirect);
            var badu = await JinaAiSearch(query, "baidu.com ", searchCount, isNoCache, isdirect);
            //var sg = await JinaAiSearch(query, "sogou.com ", searchCount, isNoCache, isdirect);
            //var tencent = await JinaAiSearch(query, "qq.com ", searchCount, isNoCache, isdirect);
            //var zhihu = await JinaAiSearch(query, "zhihu.com ", searchCount, isNoCache, isdirect);
            //var weibo = await JinaAiSearch(query, "weibo.com ", searchCount, isNoCache, isdirect);
            var dzdp = await JinaAiSearch(query, "dianping.com ", searchCount, isNoCache, isdirect);
            //var douban = await JinaAiSearch(query, "douban.com ", searchCount, isNoCache, isdirect);
            //var reddit = await JinaAiSearch(query, "reddit.com ", searchCount, isNoCache, isdirect);
            //var Wikipedia = await JinaAiSearch(query, "wikipedia.org ", searchCount, isNoCache, isdirect);

            
            
           
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
            
            //if (Wikipedia?.Data != null)
            //{
            //    _result.Data.AddRange(Wikipedia.Data);
            //}
            if (dzdp?.Data != null)
            {
                _result.Data.AddRange(dzdp.Data);
            }
            await JinaAiRerank2(query);


           return await Task.FromResult<JinaSearchResult?>(_result);
        }
        //public async Task JinaAireader(JinaSearchResult jinaSearchResult, bool isNoCache = false, bool isdirect = true)
        //{
        //    var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
        //    var apiEndpoint = $"https://r.jina.ai";

        //    var client = _httpClientFactory.CreateClient();
        //    client.Timeout = TimeSpan.FromMinutes(30);

        //    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        //    client.DefaultRequestHeaders.Add("Accept", "application/json");
        //    client.DefaultRequestHeaders.Add("X-Retain-Images", "none");
        //    //client.DefaultRequestHeaders.Add("X-Return-Format", "text");
        //    if (isdirect)
        //    {
        //        client.DefaultRequestHeaders.Add("X-Engine", "direct");
        //    }
        //    if (isNoCache)
        //    {
        //        client.DefaultRequestHeaders.Add("X-No-Cache", "true");
        //    }

        //    foreach (var item in jinaSearchResult.Data)
        //    {

        //        var requestContent = new

        //        {
        //            url = item.Url,


        //        };

        //        using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
        //        {
        //            Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
        //        }, HttpCompletionOption.ResponseHeadersRead))
        //        {
        //            if (response.StatusCode == System.Net.HttpStatusCode.OK)
        //            {
        //                response.EnsureSuccessStatusCode();
        //                string rsteing = await response.Content.ReadAsStringAsync();
        //                var jinaSearchResult1 = JsonSerializer.Deserialize<JinaReaderResult>(rsteing);

        //                if (jinaSearchResult != null)
        //                {
        //                    item.Content = jinaSearchResult1.Data.Content;
        //                }

        //            }

        //        }
        //    }
        //}
        public async Task<JinaSearchResult?> JinaAiSearch( string query, string site, int searchCount = 5, bool isNoCache = false, bool isdirect = true)
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
                q =   query,
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
                    var jinaSearchResult = JsonSerializer.Deserialize<JinaSearchResult>(rsteing,_jsonOptions);

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
                            for (int k = 0; k <_result.Data.Count; k++)
                            {
                                if (_result.Data[k].Url.Equals(jinaSearchResult.Data[i].Url, StringComparison.Ordinal))
                                {
                                    isRemove=true;
                                    break;
                                }
                            }
                            if(isRemove)
                            {
                                jinaSearchResult.Data.RemoveAt(i);
                            }
                        }
                        if(jinaSearchResult.Data.Count==0)
                        {
                            return await Task.FromResult<JinaSearchResult?>(jinaSearchResult);
                        }

                        await JinaAireader(jinaSearchResult, isNoCache, isdirect);
                        
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
        public async Task JinaAireader(JinaSearchResult jinaSearchResult, bool isNoCache = false, bool isdirect = true)
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
                        var jinaSearchResult1 = JsonSerializer.Deserialize<JinaReaderResult>(rsteing,_jsonOptions);

                        if (jinaSearchResult1 != null)
                        {
                            item.Content = jinaSearchResult1.Data.Content;
                        }
                    }
                }
            });

            await Task.WhenAll(tasks);
        }
        public async Task JinaAiRerank(string query, int rerankCount = 3)
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

        public async Task JinaAiRerank1(string query)
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
                        if (_result.Data[i].Content == _result.Data[k].Content|| _result.Data[i].Url.Equals(_result.Data[k].Url,StringComparison.Ordinal))
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
                    content = ((new StringBuilder()).Append("在<info></info>中请提取和")
                     .Append(query)
                    .Append("相关的原始信息，如果不能提取原始信息则回答:[NOT]")
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
        public async Task JinaAiRerank2(string query)
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
                contents.Add(new { role = "system", content= "你是一个优秀的资料整理专家" });
                contents.Add(new
                {
                    role = "user",
                    content = ((new StringBuilder()).Append("在<info></info>中请提取和")
                     .Append(query)
                    .Append("相关的原始信息，如果不能提取原始信息则回答:[NOT]")
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
}
