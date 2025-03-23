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

            var apiKey = Environment.GetEnvironmentVariable(chatModelConfig.EnvironmentApikeyName);
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentNullException(nameof(apiKey), "API key cannot be null or empty.");
            }
            var apiEndpointUri = new Uri(chatModelConfig.ApiEndpoint);

            var openAIClientOptions = new OpenAI.OpenAIClientOptions();
            openAIClientOptions.Endpoint = apiEndpointUri;
            

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
                yield return await Task.FromResult((await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text);
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
                yield return await Task.FromResult((await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text);
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
                List<ChatMessageContentPart> contentlist = new List<ChatMessageContentPart>();

                contentlist.Add(ChatMessageContentPart.CreateTextPart(historyMessage.Content));
                foreach (var image in historyMessage.Images)
                {
                    contentlist.Add(ChatMessageContentPart.CreateImagePart(new BinaryData(ConvertUrlToBase64(image)), "image/jpeg"));

                }
                var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new UserChatMessage(contentlist)
            };


                yield return await Task.FromResult((await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text);
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
                var messages = new List<OpenAI.Chat.ChatMessage>();
                if (!string.IsNullOrWhiteSpace(_chatModelConfig.Systemprompt))
                {
                    messages.Add(new SystemChatMessage(_chatModelConfig.Systemprompt));
                }
                foreach (var message in historyMessages)
                {
                    List<ChatMessageContentPart> contentlist = new List<ChatMessageContentPart>();
                    contentlist.Add(ChatMessageContentPart.CreateTextPart(message.Content));
                    foreach (var image in message.Images)
                    {
                        contentlist.Add(ChatMessageContentPart.CreateImagePart(new BinaryData(ConvertUrl(image)), "image/jpeg"));
                    }

                    messages.Add(new UserChatMessage(contentlist));
                }

                yield return await Task.FromResult((await _chatClient.CompleteChatAsync(messages, _options)).Value.Content[0].Text);
            }
        }

        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(string str)
        {
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new UserChatMessage(str)
            };
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
                else
                {
                    if (completionUpdate.ReasoningContentUpdate.Count > 0)
                    {
                        reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;

                    }
                    else
                    {
                        yield return string.Empty;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(reasoning_content))
                {
                    if (!beging)
                    {
                        yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
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
                        yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                        end = true;
                    }
                    else
                    {
                        if (content == "<think>" && !beging1 && !end1)
                        {
                            yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                            beging1 = true;
                        }
                        else
                        {
                            if (content == "</think>" && beging1 && !end1)
                            {
                                yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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
        }
        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(List<OpenAI.Chat.ChatMessage> messages)
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
                else
                {
                    if (completionUpdate.ReasoningContentUpdate.Count > 0)
                    {
                        reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;

                    }
                    else
                    {
                        yield return string.Empty;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(reasoning_content))
                {
                    if (!beging)
                    {
                        yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
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
                        yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                        end = true;
                    }
                    else
                    {
                        if (content == "<think>" && !beging1 && !end1)
                        {
                            yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                            beging1 = true;
                        }
                        else
                        {
                            if (content == "</think>" && beging1 && !end1)
                            {
                                yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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
        }
        
        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(HistoryMessage historyMessage)
        {
            List<ChatMessageContentPart> contentlist = new List<ChatMessageContentPart>();

            contentlist.Add(ChatMessageContentPart.CreateTextPart(historyMessage.Content));
            foreach (var image in historyMessage.Images)
            {
                contentlist.Add(ChatMessageContentPart.CreateImagePart(new BinaryData(ConvertUrl(image)), "image/jpeg"));

            }
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new UserChatMessage(contentlist)
            };

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
                else
                {
                    if (completionUpdate.ReasoningContentUpdate.Count > 0)
                    {
                        reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;

                    }
                    else
                    {
                        yield return string.Empty;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(reasoning_content))
                {
                    if (!beging)
                    {
                        yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
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
                        yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                        end = true;
                    }
                    else
                    {
                        if (content == "<think>" && !beging1 && !end1)
                        {
                            yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                            beging1 = true;
                        }
                        else
                        {
                            if (content == "</think>" && beging1 && !end1)
                            {
                                yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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
        }
        public async IAsyncEnumerable<string> CompleteChatStreamingAsync(List<HistoryMessage> historyMessages)
        {
            var messages = new List<OpenAI.Chat.ChatMessage>();
            if (!string.IsNullOrWhiteSpace(_chatModelConfig.Systemprompt))
            {
                messages.Add(new SystemChatMessage(_chatModelConfig.Systemprompt));
            }
            foreach (var message in historyMessages)
            {
                List<ChatMessageContentPart> contentlist = new List<ChatMessageContentPart>();
                contentlist.Add(ChatMessageContentPart.CreateTextPart(message.Content));
                foreach (var image in message.Images)
                {
                    contentlist.Add(ChatMessageContentPart.CreateImagePart(new BinaryData(ConvertUrl(image)), "image/jpeg"));
                }

                messages.Add(new UserChatMessage(contentlist));
            }

            
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
                else
                {
                    if (completionUpdate.ReasoningContentUpdate.Count > 0)
                    {
                        reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;
                        
                    }
                    else
                    {
                        yield return string.Empty;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(reasoning_content))
                {
                    if (!beging)
                    {
                        yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
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
                        yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                        end = true;
                    }
                    else
                    {
                        if (content == "<think>" && !beging1 && !end1)
                        {
                            yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                            beging1 = true;
                        }
                        else
                        {
                            if (content == "</think>" && beging1 && !end1)
                            {
                                yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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
        }
        public async IAsyncEnumerable<string> CompleteChatStreamingAsync1(List<HistoryMessage> historyMessages)
        {
            var messages = new List<OpenAI.Chat.ChatMessage>();
            if (!string.IsNullOrWhiteSpace(_chatModelConfig.Systemprompt))
            {
                messages.Add(new SystemChatMessage(_chatModelConfig.Systemprompt));
            }
            foreach (var message in historyMessages)
            {
                List<ChatMessageContentPart> contentlist = new List<ChatMessageContentPart>();
                contentlist.Add(ChatMessageContentPart.CreateTextPart(message.Content));
                foreach (var image in message.Images)
                {
                    contentlist.Add(ChatMessageContentPart.CreateImagePart(new BinaryData(ConvertUrl(image)), "image/jpeg"));
                }

                messages.Add(new UserChatMessage(contentlist));
            }
            InitTool();

            bool requiresAction;
            
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

                    if (completionUpdate.ToolCallUpdates.Count==0)
                    {
                        string content = string.Empty;
                        string reasoning_content = string.Empty;

                        if (completionUpdate.ContentUpdate.Count > 0)
                        {
                            content = completionUpdate.ContentUpdate[0].Text;

                        }
                        else
                        {
                            if (completionUpdate.ReasoningContentUpdate.Count > 0)
                            {
                                reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;

                            }
                            else
                            {
                                //yield return string.Empty;
                                //break;
                            }
                        }
                        if (!string.IsNullOrEmpty(reasoning_content))
                        {
                            if (!beging)
                            {
                                yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
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
                                yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                                end = true;
                            }
                            else
                            {
                                if (content == "<think>" && !beging1 && !end1)
                                {
                                    yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                                    beging1 = true;
                                }
                                else
                                {
                                    if (content == "</think>" && beging1 && !end1)
                                    {
                                        yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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

                    switch (completionUpdate.FinishReason)
                    {
                        case ChatFinishReason.Stop:
                            {
                                

                                messages.Add(new AssistantChatMessage(contentBuilder.ToString()));
                                break;
                            }

                        case ChatFinishReason.ToolCalls:
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
                                    switch (toolCall.FunctionName)
                                    {

                                        case nameof(JinaAiSearch):
                                            {

                                                using JsonDocument argumentsJson = JsonDocument.Parse(toolCall.FunctionArguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }

                                                string toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));

                                                messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                                                break;
                                            }

                                        default:
                                            {
                                                throw new NotImplementedException();
                                            }
                                    }
                                }

                                requiresAction = true;
                                break;
                            }

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


            //bool beging = false;
            //bool end = false;
            //bool beging1 = false;
            //bool end1 = false;
            //await foreach (StreamingChatCompletionUpdate completionUpdate in _chatClient.CompleteChatStreamingAsync(messages, _options))
            //{
            //    string content = string.Empty;
            //    string reasoning_content = string.Empty;

            //    if (completionUpdate.ContentUpdate.Count > 0)
            //    {
            //        content = completionUpdate.ContentUpdate[0].Text;

            //    }
            //    else
            //    {
            //        if (completionUpdate.ReasoningContentUpdate.Count > 0)
            //        {
            //            reasoning_content = completionUpdate.ReasoningContentUpdate[0].Text;

            //        }
            //        else
            //        {
            //            yield return string.Empty;
            //            break;
            //        }
            //    }
            //    if (!string.IsNullOrEmpty(reasoning_content))
            //    {
            //        if (!beging)
            //        {
            //            yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
            //            beging = true;
            //        }
            //        else
            //        {
            //            yield return reasoning_content;

            //        }

            //    }
            //    if (!string.IsNullOrEmpty(content))
            //    {
            //        if (beging && !end)
            //        {
            //            yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
            //            end = true;
            //        }
            //        else
            //        {
            //            if (content == "<think>" && !beging1 && !end1)
            //            {
            //                yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
            //                beging1 = true;
            //            }
            //            else
            //            {
            //                if (content == "</think>" && beging1 && !end1)
            //                {
            //                    yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
            //                    end1 = true;
            //                }
            //                else
            //                {
            //                    yield return content;
            //                }

            //            }

            //        }

            //    }
            //}
        }
        private void InitTool()
        {
           var tool= OpenAI.Chat.ChatTool.CreateFunctionTool(
                functionName: nameof(JinaAiSearch),
                functionDescription: "执行网页搜索并返回结果",
                functionParameters: BinaryData.FromBytes(
                """
                {
                    "type": "object",
                    "properties": {
                        "query": { "type": "string" }
                       
                    },
                    "required": [ "query" ]
                }

                """u8.ToArray()
                )
            );
            _options.Tools.Add(tool);


        }
        public ChatModelConfig ChatModelConfig { get => _chatModelConfig; set => _chatModelConfig = value; }

        private async Task<string>? JinaAiSearch(string query)
        {
            var result = await   _jinaSearch.Search(query);
            return await Task.FromResult(result);
        }
       

        // 修改 ConvertUrlToBase64 方法，使用 ImageSharp 库进行图片压缩
        private  string ConvertUrlToBase64(string imageUrl)
        {
            // 下载并压缩图片
            using (var client = _httpClientFactory.CreateClient())
            {
                byte[] imageBytesOriginal = client.GetByteArrayAsync(imageUrl).Result;

                using (var ms = new MemoryStream(imageBytesOriginal))
                {
                    // 加载图片
                    using (var image = SixLabors.ImageSharp.Image.Load(ms))
                    {
                        // 可选：调整图片尺寸
                        int maxWidth = 1024;
                        if (image.Width > maxWidth)
                        {
                            var ratio = (double)maxWidth / image.Width;
                            int newHeight = (int)(image.Height * ratio);
                            image.Mutate(x => x.Resize(maxWidth, newHeight));
                        }

                        //// 设置压缩质量和选择编码器
                        //var encoder = image.Metadata.DecodedImageFormat.Name switch
                        //{
                        //    "JPEG" => (IImageEncoder)new JpegEncoder { Quality = 80 }, // 压缩质量，范围0-100
                        //    "PNG" => new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 },
                        //    "GIF" => new GifEncoder(), // 支持 GIF 格式
                        //    "WEBP" => new WebpEncoder(), // 支持 WEBP 格式
                        //    _ => new JpegEncoder { Quality = 80 } // 默认使用 JPEG 编码
                        //};
                        // 设置压缩质量
                        var encoder = new JpegEncoder
                        {
                            Quality = 80 // 压缩质量，范围0-100
                        };

                        using (var msCompressed = new MemoryStream())
                        {
                            // 保存压缩后的图片到内存流
                            image.Save(msCompressed, encoder);

                            // 转换为Base64字符串
                            return Convert.ToBase64String(msCompressed.ToArray());
                        }
                    }
                }
            }
        }

        // 修改 ConvertUrlToBase64 方法，使用 ImageSharp 库进行图片压缩
        private  byte[] ConvertUrl(string imageUrl)
        {
            // 下载并压缩图片
            using (var client = _httpClientFactory.CreateClient())
            {
                byte[] imageBytesOriginal = client.GetByteArrayAsync(imageUrl).Result;

                using (var ms = new MemoryStream(imageBytesOriginal))
                {
                    // 加载图片
                    using (var image = SixLabors.ImageSharp.Image.Load(ms))
                    {
                        // 可选：调整图片尺寸
                        int maxWidth = 1024;
                        if (image.Width > maxWidth)
                        {
                            var ratio = (double)maxWidth / image.Width;
                            int newHeight = (int)(image.Height * ratio);
                            image.Mutate(x => x.Resize(maxWidth, newHeight));
                        }

                        //// 设置压缩质量和选择编码器
                        //var encoder = image.Metadata.DecodedImageFormat.Name switch
                        //{
                        //    "JPEG" => (IImageEncoder)new JpegEncoder { Quality = 80 }, // 压缩质量，范围0-100
                        //    "PNG" => new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 },
                        //    "GIF" => new GifEncoder(), // 支持 GIF 格式
                        //    "WEBP" => new WebpEncoder(), // 支持 WEBP 格式
                        //    _ => new JpegEncoder { Quality = 80 } // 默认使用 JPEG 编码
                        //};
                        // 设置压缩质量
                        var encoder = new JpegEncoder
                        {
                            Quality = 80 // 压缩质量，范围0-100
                        };

                        using (var msCompressed = new MemoryStream())
                        {
                            // 保存压缩后的图片到内存流
                            image.Save(msCompressed, encoder);

                            // 转换为Base64字符串
                            return msCompressed.ToArray();
                        }
                    }
                }
            }
        }
    }

    // 辅助类
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
            // Keep track of which tool call ID belongs to this update index.
            if (toolCallUpdate.ToolCallId != null)
            {
                _indexToToolCallId[toolCallUpdate.Index] = toolCallUpdate.ToolCallId;
            }

            // Keep track of which function name belongs to this update index.
            if (toolCallUpdate.FunctionName != null)
            {
                _indexToFunctionName[toolCallUpdate.Index] = toolCallUpdate.FunctionName;
            }

            // Keep track of which function arguments belong to this update index,
            // and accumulate the arguments as new updates arrive.
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
