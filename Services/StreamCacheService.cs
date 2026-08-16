using System.Collections.Concurrent;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// 流式消息缓存服务，用于支持断线重连
    /// </summary>
    public class StreamCacheService
    {
        private readonly ConcurrentDictionary<string, StreamCacheItem> _cache = new();
        private readonly TimeSpan _expirationTime = TimeSpan.FromMinutes(5);
        private readonly Timer _cleanupTimer;

        public StreamCacheService()
        {
            // 每分钟清理过期缓存
            _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// 创建新的流式缓存
        /// </summary>
        public string CreateStream()
        {
            var streamId = Guid.NewGuid().ToString("N");
            _cache[streamId] = new StreamCacheItem
            {
                Content = new System.Text.StringBuilder(),
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                IsCompleted = false
            };
            return streamId;
        }

        /// <summary>
        /// 追加内容到缓存
        /// </summary>
        public void AppendContent(string streamId, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            if (_cache.TryGetValue(streamId, out var item))
            {
                lock (item.SyncLock)
                {
                    item.Content.Append(content);
                    item.LastUpdatedAt = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// 保存当前 Responses 响应 ID，供断线恢复后继续 previous_response_id 链路。
        /// </summary>
        public void SetResponseId(string streamId, string? responseId)
        {
            if (string.IsNullOrWhiteSpace(responseId) ||
                !_cache.TryGetValue(streamId, out var item))
            {
                return;
            }

            lock (item.SyncLock)
            {
                item.ResponseId = responseId;
                item.LastUpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// 标记流式传输完成
        /// </summary>
        public void MarkCompleted(string streamId)
        {
            if (_cache.TryGetValue(streamId, out var item))
            {
                lock (item.SyncLock)
                {
                    item.IsCompleted = true;
                    item.LastUpdatedAt = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// 获取从指定偏移量开始的内容
        /// </summary>
        public StreamResumeResult? GetContentFromOffset(string streamId, int offset)
        {
            if (!_cache.TryGetValue(streamId, out var item))
            {
                return null;
            }

            offset = Math.Max(0, offset);

            lock (item.SyncLock)
            {
                var fullContent = item.Content.ToString();
                if (offset >= fullContent.Length)
                {
                    return new StreamResumeResult
                    {
                        Content = string.Empty,
                        TotalLength = fullContent.Length,
                        IsCompleted = item.IsCompleted,
                        ResponseId = item.ResponseId
                    };
                }

                return new StreamResumeResult
                {
                    Content = fullContent[offset..],
                    TotalLength = fullContent.Length,
                    IsCompleted = item.IsCompleted,
                    ResponseId = item.ResponseId
                };
            }
        }

        /// <summary>
        /// 检查流是否存在
        /// </summary>
        public bool StreamExists(string streamId)
        {
            return _cache.ContainsKey(streamId);
        }

        /// <summary>
        /// 清理过期缓存
        /// </summary>
        private void CleanupExpiredItems(object? state)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cache
                .Where(kvp => now - kvp.Value.CreatedAt > _expirationTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _cache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// 移除指定流缓存
        /// </summary>
        public void RemoveStream(string streamId)
        {
            _cache.TryRemove(streamId, out _);
        }
    }

    /// <summary>
    /// 流式缓存项
    /// </summary>
    public class StreamCacheItem
    {
        public System.Text.StringBuilder Content { get; set; } = new();
        public Lock SyncLock { get; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
        public bool IsCompleted { get; set; }
        public string? ResponseId { get; set; }
    }

    /// <summary>
    /// 流式恢复结果
    /// </summary>
    public class StreamResumeResult
    {
        public string Content { get; set; } = string.Empty;
        public int TotalLength { get; set; }
        public bool IsCompleted { get; set; }
        public string? ResponseId { get; set; }
    }
}
