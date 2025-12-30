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
                IsCompleted = false
            };
            return streamId;
        }

        /// <summary>
        /// 追加内容到缓存
        /// </summary>
        public void AppendContent(string streamId, string content)
        {
            if (_cache.TryGetValue(streamId, out var item))
            {
                lock (item.Content)
                {
                    item.Content.Append(content);
                    item.LastUpdatedAt = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// 标记流式传输完成
        /// </summary>
        public void MarkCompleted(string streamId)
        {
            if (_cache.TryGetValue(streamId, out var item))
            {
                item.IsCompleted = true;
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

            lock (item.Content)
            {
                var fullContent = item.Content.ToString();
                if (offset >= fullContent.Length)
                {
                    return new StreamResumeResult
                    {
                        Content = string.Empty,
                        TotalLength = fullContent.Length,
                        IsCompleted = item.IsCompleted
                    };
                }

                return new StreamResumeResult
                {
                    Content = fullContent.Substring(offset),
                    TotalLength = fullContent.Length,
                    IsCompleted = item.IsCompleted
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
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
        public bool IsCompleted { get; set; }
    }

    /// <summary>
    /// 流式恢复结果
    /// </summary>
    public class StreamResumeResult
    {
        public string Content { get; set; } = string.Empty;
        public int TotalLength { get; set; }
        public bool IsCompleted { get; set; }
    }
}
