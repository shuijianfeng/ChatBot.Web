using System.Buffers;
using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ChatBot.Web.Services
{
    public class OpenAIService
    {
        private readonly ChatClient _chatClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ChatCompletionOptions _options;
        private ChatModelConfig _chatModelConfig;
        private readonly JinaSearch _jinaSearch;
        private readonly OpenWeather _openWeather;

        public OpenAIService(ChatModelConfig chatModelConfig, IHttpClientFactory httpClientFactory)
        {
            _chatModelConfig = chatModelConfig;
            _httpClientFactory = httpClientFactory;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            _jinaSearch = new JinaSearch(httpClientFactory);
            _openWeather = new OpenWeather(_httpClientFactory);
            var apiKey = Environment.GetEnvironmentVariable(chatModelConfig.EnvironmentApikeyName);
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentNullException(nameof(apiKey), "API key cannot be null or empty.");
            }
            var apiEndpointUri = new Uri(chatModelConfig.ApiEndpoint);

            var openAIClientOptions = new OpenAI.OpenAIClientOptions
            {
                Endpoint = apiEndpointUri
            };

            _chatClient = new ChatClient(_chatModelConfig.Model, new ApiKeyCredential(apiKey), openAIClientOptions);

            _options = new ChatCompletionOptions();
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(string str)
        {
            await foreach (var item in CompleteChatAsync(str, _chatModelConfig.Stream))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(List<OpenAI.Chat.ChatMessage> messages)
        {
            await foreach (var item in CompleteChatAsync(messages, _chatModelConfig.Stream))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(HistoryMessage historyMessage)
        {
            await foreach (var item in CompleteChatAsync(historyMessage, _chatModelConfig.Stream))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(List<HistoryMessage> historyMessages)
        {
            await foreach (var item in CompleteChatAsync(historyMessages, _chatModelConfig.Stream))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(string str, bool isStream)
        {
            if (isStream)
            {
                await foreach (var item in CompleteChatStreamingAsync(str))
                {
                    yield return item;
                }
            }
            else
            {
                var messages = new List<OpenAI.Chat.ChatMessage>
                {
                    new UserChatMessage(str)
                };
                yield return (await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(List<OpenAI.Chat.ChatMessage> messages, bool isStream)
        {
            if (isStream)
            {
                await foreach (var item in CompleteChatStreamingAsync(messages))
                {
                    yield return item;
                }
            }
            else
            {
                yield return (await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(HistoryMessage historyMessage, bool isStream)
        {
            if (isStream)
            {
                await foreach (var item in CompleteChatStreamingAsync(historyMessage))
                {
                    yield return item;
                }
            }
            else
            {
                var messages = await BuildMessagesFromHistoryAsync(new List<HistoryMessage> { historyMessage });
                yield return (await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatAsync(List<HistoryMessage> historyMessages, bool isStream)
        {
            if (isStream)
            {
                await foreach (var item in CompleteChatStreamingAsync(historyMessages))
                {
                    yield return item;
                }
            }
            else
            {
                var messages = await BuildMessagesFromHistoryAsync(historyMessages);
                yield return (await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(string str)
        {
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new UserChatMessage(str)
            };
            await foreach (var item in ProcessStreamingResponseAsync(messages))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(List<OpenAI.Chat.ChatMessage> messages)
        {
            await foreach (var item in ProcessStreamingResponseAsync(messages))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(HistoryMessage historyMessage)
        {
            var messages = await BuildMessagesFromHistoryAsync(new List<HistoryMessage> { historyMessage });
            await foreach (var item in ProcessStreamingResponseAsync(messages))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(List<HistoryMessage> historyMessages)
        {
            var messages = await BuildMessagesFromHistoryAsync(historyMessages);
            await foreach (var item in ProcessStreamingResponseAsync(messages))
            {
                yield return item;
            }
        }

        // 提取重复的流处理逻辑
        private async IAsyncEnumerable<string> ProcessStreamingResponseAsync(List<OpenAI.Chat.ChatMessage> messages)
        {
            bool beging = false;
            bool end = false;
            bool beging1 = false;
            bool end1 = false;

            await foreach (StreamingChatCompletionUpdate completionUpdate in _chatClient.CompleteChatStreamingAsync(messages, _options))
            {
                string content = string.Empty;
                string reasoning_content = string.Empty;

                if (completionUpdate.ContentUpdate.Count > 0)
                {
                    content = completionUpdate.ContentUpdate[0].Text;
                }
                else if (completionUpdate.ReasoningContentUpdate.Count > 0)
                {
                    reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;
                }
                else
                {
                    yield return string.Empty;
                    break;
                }

                if (!string.IsNullOrEmpty(reasoning_content))
                {
                    if (!beging)
                    {
                        yield return "<think>" + "\n" + "\n" + "~~~Thoughts" + "\n" + "\n" + reasoning_content;
                        beging = true;
                    }
                    else
                    {
                        yield return reasoning_content;
                    }
                }

                if (!string.IsNullOrEmpty(content))
                {
                    if (beging && !end)
                    {
                        yield return "\n" + "\n" + "~~~" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                        end = true;
                    }
                    else
                    {
                        if (content == "<think>" && !beging1 && !end1)
                        {
                            yield return content + "\n" + "\n" + "~~~Thoughts" + "\n" + "\n";
                            beging1 = true;
                        }
                        else if (content == "</think>" && beging1 && !end1)
                        {
                            yield return "\n" + "\n" + "~~~" + "\n" + "\n" + content + "\n";
                            end1 = true;
                        }
                        else
                        {
                            yield return content;
                        }
                    }
                }
            }
        }

        // 辅助方法:从历史消息构建聊天消息列表
        private async Task<List<OpenAI.Chat.ChatMessage>> BuildMessagesFromHistoryAsync(List<HistoryMessage> historyMessages)
        {
            var messages = new List<OpenAI.Chat.ChatMessage>();
            if (!string.IsNullOrWhiteSpace(_chatModelConfig.Systemprompt))
            {
                messages.Add(new SystemChatMessage(_chatModelConfig.Systemprompt));
            }

            foreach (var message in historyMessages)
            {
                List<ChatMessageContentPart> contentlist = new List<ChatMessageContentPart>
                {
                    ChatMessageContentPart.CreateTextPart(message.Content)
                };

                foreach (var image in message.Images)
                {
                    var imageBytes = await ConvertUrlAsync(image);
                    contentlist.Add(ChatMessageContentPart.CreateImagePart(new BinaryData(imageBytes), "image/jpeg"));
                }

                messages.Add(new UserChatMessage(contentlist));
            }

            return messages;
        }

        public async IAsyncEnumerable<string> CompleteChatStreamingAsync1(List<HistoryMessage> historyMessages)
        {
            var messages = await BuildMessagesFromHistoryAsync(historyMessages);
            InitTool();
            bool requiresAction;

            if (_chatModelConfig.Stream)
            {
                do
                {
                    requiresAction = false;
                    StringBuilder contentBuilder = new();
                    StreamingChatToolCallsBuilder toolCallsBuilder = new();

                    AsyncCollectionResult<StreamingChatCompletionUpdate> completionUpdates = _chatClient.CompleteChatStreamingAsync(messages, _options);
                    bool beging = false;
                    bool end = false;
                    bool beging1 = false;
                    bool end1 = false;

                    await foreach (StreamingChatCompletionUpdate completionUpdate in completionUpdates)
                    {
                        foreach (ChatMessageContentPart contentPart in completionUpdate.ContentUpdate)
                        {
                            contentBuilder.Append(contentPart.Text);
                        }

                        foreach (StreamingChatToolCallUpdate toolCallUpdate in completionUpdate.ToolCallUpdates)
                        {
                            toolCallsBuilder.Append(toolCallUpdate);
                        }

                        if (completionUpdate.ToolCallUpdates.Count == 0)
                        {
                            string content = string.Empty;
                            string reasoning_content = string.Empty;

                            if (completionUpdate.ContentUpdate.Count > 0)
                            {
                                content = completionUpdate.ContentUpdate[0].Text;
                            }
                            else if (completionUpdate.ReasoningContentUpdate.Count > 0)
                            {
                                reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;
                            }

                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                if (!beging)
                                {
                                    yield return "<think>" + "\n" + "\n" + "~~~Thoughts" + "\n" + "\n" + reasoning_content;
                                    beging = true;
                                }
                                else
                                {
                                    yield return reasoning_content;
                                }
                            }

                            if (!string.IsNullOrEmpty(content))
                            {
                                if (beging && !end)
                                {
                                    yield return "\n" + "\n" + "~~~" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                                    end = true;
                                }
                                else if (content == "<think>" && !beging1 && !end1)
                                {
                                    yield return content + "\n" + "\n" + "~~~Thoughts" + "\n" + "\n";
                                    beging1 = true;
                                }
                                else if (content == "</think>" && beging1 && !end1)
                                {
                                    yield return "\n" + "\n" + "~~~" + "\n" + "\n" + content + "\n";
                                    end1 = true;
                                }
                                else
                                {
                                    yield return content;
                                }
                            }
                        }

                        switch (completionUpdate.FinishReason)
                        {
                            case ChatFinishReason.Stop:
                                messages.Add(new AssistantChatMessage(contentBuilder.ToString()));
                                break;

                            case ChatFinishReason.ToolCalls:
                                await ProcessToolCallsAsync(messages, contentBuilder, toolCallsBuilder);
                                requiresAction = true;
                                break;

                            case ChatFinishReason.Length:
                                throw new NotImplementedException("Incomplete model output due to MaxTokens parameter or token limit exceeded.");

                            case ChatFinishReason.ContentFilter:
                                throw new NotImplementedException("Omitted content due to a content filter flag.");

                            case ChatFinishReason.FunctionCall:
                                throw new NotImplementedException("Deprecated in favor of tool calls.");

                            case null:
                                break;
                        }
                    }
                } while (requiresAction);
            }
            else
            {
                await foreach (var item in ProcessStreamingResponseAsync(messages))
                {
                    yield return item;
                }
            }
        }

        // 提取工具调用处理逻辑
        private async Task ProcessToolCallsAsync(List<OpenAI.Chat.ChatMessage> messages, StringBuilder contentBuilder, StreamingChatToolCallsBuilder toolCallsBuilder)
        {
            IReadOnlyList<ChatToolCall> toolCalls = toolCallsBuilder.Build();
            AssistantChatMessage assistantMessage = new(toolCalls);

            if (contentBuilder.Length > 0)
            {
                assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(contentBuilder.ToString()));
            }

            messages.Add(assistantMessage);

            foreach (ChatToolCall toolCall in toolCalls)
            {
                string toolResult = toolCall.FunctionName switch
                {
                    nameof(GetCurrentDataTime) => await GetCurrentDataTime(),
                    nameof(JinaAiSearch) => await ProcessJinaSearchAsync(toolCall),
                    nameof(SearchTrainTicket) => await ProcessTrainTicketSearchAsync(toolCall),
                    nameof(GetWeather) => await ProcessWeatherAsync(toolCall),
                    _ => throw new NotImplementedException($"Tool function '{toolCall.FunctionName}' is not implemented.")
                };

                messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
            }
        }

        private async Task<string> ProcessJinaSearchAsync(ChatToolCall toolCall)
        {
            using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
            if (!argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery))
            {
                throw new ArgumentNullException("query", "The query argument is required.");
            }
            return await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException("query", "Query cannot be null."));
        }

        private async Task<string> ProcessTrainTicketSearchAsync(ChatToolCall toolCall)
        {
            using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
            bool hasStartingPlace = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);
            bool hasArrivalPlace = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
            bool hasDate = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);

            if (!hasStartingPlace || !hasArrivalPlace || !hasDate)
            {
                throw new ArgumentNullException("Required parameters missing for train ticket search.");
            }

            return await SearchTrainTicket(
                startingplace.GetString() ?? throw new ArgumentNullException(nameof(startingplace)),
                arrivalplace.GetString() ?? throw new ArgumentNullException(nameof(arrivalplace)),
                date.GetString() ?? throw new ArgumentNullException(nameof(date)));
        }

        private async Task<string> ProcessWeatherAsync(ChatToolCall toolCall)
        {
            using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
            if (!argumentsJson.RootElement.TryGetProperty("city", out JsonElement city))
            {
                throw new ArgumentNullException("city", "The city parameter is required.");
            }
            return await GetWeather(city.GetString() ?? throw new ArgumentNullException("city", "City cannot be null."));
        }

        private void InitTool()
        {
            // 添加网页搜索工具
            var searchTool = OpenAI.Chat.ChatTool.CreateFunctionTool(
                functionName: nameof(JinaAiSearch),
                functionDescription: "执行网页搜索并返回结果",
                functionParameters: BinaryData.FromBytes(
                """
        {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "搜索词"
                }
            },
            "required": [ "query" ]
        }
        """u8.ToArray()
                )
            );
            _options.Tools.Add(searchTool);

            // 添加天气查询工具
            var weatherTool = OpenAI.Chat.ChatTool.CreateFunctionTool(
                functionName: nameof(GetWeather),
                functionDescription: "获取指定城市未来8天天气预报",
                functionParameters: BinaryData.FromBytes(
                """
        {
            "type": "object",
            "properties": {
                "city": {
                    "type": "string",
                    "description": "城市(用英文表示)"
                }
            },
            "required": [ "city" ]
        }
        """u8.ToArray()
                )
            );
            _options.Tools.Add(weatherTool);

            // 添加获取当前日期时间工具
            var timeTool = OpenAI.Chat.ChatTool.CreateFunctionTool(
                functionName: nameof(GetCurrentDataTime),
                functionDescription: "获取当前日期和时间",
                functionParameters: BinaryData.FromBytes(
                """
        {
            "type": "object",
            "properties": {}
        }
        """u8.ToArray()
                )
            );
            _options.Tools.Add(timeTool);

            // 添加火车票查询工具
            var trainTool = OpenAI.Chat.ChatTool.CreateFunctionTool(
                functionName: nameof(SearchTrainTicket),
                functionDescription: "获取指定日期的火车票、火车车次",
                functionParameters: BinaryData.FromBytes(
                """
        {
            "type": "object",
            "properties": {
                "startingplace": {
                    "type": "string",
                    "description": "起始城市"
                },
                "arrivalplace": {
                    "type": "string",
                    "description": "到达城市"
                },
                "date": {
                    "type": "string",
                    "description": "日期(查询日期需要大于或等于今天日期,格式:YYYY-MM-DD)"
                }
            },
            "required": [ "startingplace", "arrivalplace", "date" ]
        }
        """u8.ToArray()
                )
            );
            _options.Tools.Add(trainTool);
        }

        public ChatModelConfig ChatModelConfig { get => _chatModelConfig; set => _chatModelConfig = value; }

        private async Task<string> JinaAiSearch(string query)
        {
            return await _jinaSearch.Search(query);
        }

        private async Task<string> GetWeather(string query)
        {
            return await _openWeather.GetWeatherAsync(query);
        }

        private async Task<string> SearchTrainTicket(string startingplace, string arrivalplace, string date)
        {
            return await _jinaSearch.SearchTrainTicket(startingplace, arrivalplace, date);
        }

        private Task<string> GetCurrentDataTime()
        {
            var result = DateTime.Now.ToString(" 日期: yyyy年M月dd日 dddd 时间：HH:mm:ss ");
            return Task.FromResult(result);
        }

        // 修复: 异步方法,避免 .Result 阻塞
        private async Task<string> ConvertUrlToBase64Async(string imageUrl)
        {
            using var client = _httpClientFactory.CreateClient();
            byte[] imageBytesOriginal = await client.GetByteArrayAsync(imageUrl);

            using var ms = new MemoryStream(imageBytesOriginal);
            using var image = await SixLabors.ImageSharp.Image.LoadAsync(ms);

            // 调整图片尺寸
            int maxWidth = 1024;
            if (image.Width > maxWidth)
            {
                var ratio = (double)maxWidth / image.Width;
                int newHeight = (int)(image.Height * ratio);
                image.Mutate(x => x.Resize(maxWidth, newHeight));
            }

            var encoder = new JpegEncoder { Quality = 80 };
            using var msCompressed = new MemoryStream();
            await image.SaveAsync(msCompressed, encoder);
            return Convert.ToBase64String(msCompressed.ToArray());
        }

        // 修复: 异步方法,避免 .Result 阻塞
        private async Task<byte[]> ConvertUrlAsync(string imageUrl)
        {
            using var client = _httpClientFactory.CreateClient();
            byte[] imageBytesOriginal = await client.GetByteArrayAsync(imageUrl);

            using var ms = new MemoryStream(imageBytesOriginal);
            using var image = await SixLabors.ImageSharp.Image.LoadAsync(ms);

            // 调整图片尺寸
            int maxWidth = 1024;
            if (image.Width > maxWidth)
            {
                var ratio = (double)maxWidth / image.Width;
                int newHeight = (int)(image.Height * ratio);
                image.Mutate(x => x.Resize(maxWidth, newHeight));
            }

            var encoder = new JpegEncoder { Quality = 80 };
            using var msCompressed = new MemoryStream();
            await image.SaveAsync(msCompressed, encoder);
            return msCompressed.ToArray();
        }

        // 保留同步版本以保持向后兼容(如果需要)
        private string ConvertUrlToBase64(string imageUrl)
        {
            return ConvertUrlToBase64Async(imageUrl).GetAwaiter().GetResult();
        }

        private byte[] ConvertUrl(string imageUrl)
        {
            return ConvertUrlAsync(imageUrl).GetAwaiter().GetResult();
        }
    }

    // 其他辅助类保持不变...
    public class OpenAiSearchArgs
    {
        public string Query { get; set; }
        public int MaxResults { get; set; } = 5;
    }

    public class OpenAiSearchResult
    {
        public string Query { get; set; }
        public List<string> Results { get; set; }
    }

    public class StreamingChatToolCallsBuilder
    {
        private readonly Dictionary<int, string> _indexToToolCallId = [];
        private readonly Dictionary<int, string> _indexToFunctionName = [];
        private readonly Dictionary<int, SequenceBuilder<byte>> _indexToFunctionArguments = [];

        public void Append(StreamingChatToolCallUpdate toolCallUpdate)
        {
            if (toolCallUpdate.ToolCallId != null)
            {
                _indexToToolCallId[toolCallUpdate.Index] = toolCallUpdate.ToolCallId;
            }

            if (toolCallUpdate.FunctionName != null)
            {
                _indexToFunctionName[toolCallUpdate.Index] = toolCallUpdate.FunctionName;
            }

            if (toolCallUpdate.FunctionArgumentsUpdate != null && !toolCallUpdate.FunctionArgumentsUpdate.ToMemory().IsEmpty)
            {
                if (!_indexToFunctionArguments.TryGetValue(toolCallUpdate.Index, out SequenceBuilder<byte> argumentsBuilder))
                {
                    argumentsBuilder = new SequenceBuilder<byte>();
                    _indexToFunctionArguments[toolCallUpdate.Index] = argumentsBuilder;
                }

                argumentsBuilder.Append(toolCallUpdate.FunctionArgumentsUpdate);
            }
        }

        public IReadOnlyList<ChatToolCall> Build()
        {
            List<ChatToolCall> toolCalls = [];

            foreach ((int index, string toolCallId) in _indexToToolCallId)
            {
                ReadOnlySequence<byte> sequence = _indexToFunctionArguments[index].Build();

                ChatToolCall toolCall = ChatToolCall.CreateFunctionToolCall(
                    id: toolCallId,
                    functionName: _indexToFunctionName[index],
                    functionArguments: BinaryData.FromBytes(sequence.ToArray()));

                toolCalls.Add(toolCall);
            }

            return toolCalls;
        }
    }

    public class SequenceBuilder<T>
    {
        Segment _first;
        Segment _last;

        public void Append(ReadOnlyMemory<T> data)
        {
            if (_first == null)
            {
                Debug.Assert(_last == null);
                _first = new Segment(data);
                _last = _first;
            }
            else
            {
                _last = _last!.Append(data);
            }
        }

        public ReadOnlySequence<T> Build()
        {
            if (_first == null)
            {
                Debug.Assert(_last == null);
                return ReadOnlySequence<T>.Empty;
            }

            if (_first == _last)
            {
                Debug.Assert(_first.Next == null);
                return new ReadOnlySequence<T>(_first.Memory);
            }

            return new ReadOnlySequence<T>(_first, 0, _last!, _last!.Memory.Length);
        }

        private sealed class Segment : ReadOnlySequenceSegment<T>
        {
            public Segment(ReadOnlyMemory<T> items) : this(items, 0)
            {
            }

            private Segment(ReadOnlyMemory<T> items, long runningIndex)
            {
                Debug.Assert(runningIndex >= 0);
                Memory = items;
                RunningIndex = runningIndex;
            }

            public Segment Append(ReadOnlyMemory<T> items)
            {
                long runningIndex;
                checked { runningIndex = RunningIndex + Memory.Length; }
                Segment segment = new(items, runningIndex);
                Next = segment;
                return segment;
            }
        }
    }
}