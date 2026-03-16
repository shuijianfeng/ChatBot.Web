using Npgsql;
using System.Text.Json;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// 会话数据模型
    /// </summary>
    public class ChatSession
    {
        public string Id { get; set; } = string.Empty;
        public string Uid { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
    }

    /// <summary>
    /// 聊天消息数据模型
    /// </summary>
    public class ChatMessage
    {
        public int Id { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<string>? ImageUrls { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// 保存会话请求模型
    /// </summary>
    public class SaveSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string Uid { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public List<SaveMessageRequest> Messages { get; set; } = new();
    }

    public class SaveMessageRequest
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<string>? ImageUrls { get; set; }
    }

    /// <summary>
    /// 会话数据访问层，负责与 PostgreSQL 数据库交互。
    /// 使用 NpgsqlDataSource 进行连接池化管理，避免每次手动创建连接。
    /// </summary>
    public class ChatSessionRepository
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly ILogger<ChatSessionRepository> _logger;

        public ChatSessionRepository(NpgsqlDataSource dataSource, ILogger<ChatSessionRepository> logger)
        {
            _dataSource = dataSource;
            _logger = logger;
        }

        /// <summary>
        /// 确保数据库表存在
        /// </summary>
        public async Task EnsureTablesExistAsync()
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();

                var createTablesSql = @"
                    -- 会话表
                    CREATE TABLE IF NOT EXISTS chat_sessions (
                        id VARCHAR(50) PRIMARY KEY,
                        uid VARCHAR(100) NOT NULL,
                        title VARCHAR(500),
                        model_name VARCHAR(100),
                        created_at TIMESTAMP DEFAULT NOW(),
                        updated_at TIMESTAMP DEFAULT NOW()
                    );

                    -- 消息表
                    CREATE TABLE IF NOT EXISTS chat_messages (
                        id SERIAL PRIMARY KEY,
                        session_id VARCHAR(50) NOT NULL,
                        role VARCHAR(20) NOT NULL,
                        content TEXT NOT NULL,
                        image_urls TEXT[],
                        created_at TIMESTAMP DEFAULT NOW(),
                        FOREIGN KEY (session_id) REFERENCES chat_sessions(id) ON DELETE CASCADE
                    );

                    -- 索引
                    CREATE INDEX IF NOT EXISTS idx_sessions_uid ON chat_sessions(uid);
                    CREATE INDEX IF NOT EXISTS idx_messages_session ON chat_messages(session_id);
                ";

                await using var command = new NpgsqlCommand(createTablesSql, connection);
                await command.ExecuteNonQueryAsync();

                _logger.LogInformation("数据库表检查/创建完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建数据库表失败");
                throw;
            }
        }

        /// <summary>
        /// 获取用户的所有会话列表（不包含消息内容）
        /// </summary>
        public async Task<List<ChatSession>> GetSessionsByUserAsync(string uid)
        {
            var sessions = new List<ChatSession>();

            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();

                var sql = @"
                    SELECT id, uid, title, model_name, created_at, updated_at 
                    FROM chat_sessions 
                    WHERE uid = @uid 
                    ORDER BY updated_at DESC";

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("uid", uid);

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sessions.Add(new ChatSession
                    {
                        Id = reader.GetString(0),
                        Uid = reader.GetString(1),
                        Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        ModelName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        CreatedAt = reader.GetDateTime(4),
                        UpdatedAt = reader.GetDateTime(5)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户会话列表失败: uid={Uid}", uid);
            }

            return sessions;
        }

        /// <summary>
        /// 获取会话详情（包含所有消息）
        /// </summary>
        public async Task<ChatSession?> GetSessionWithMessagesAsync(string sessionId)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();

                // 获取会话基本信息
                var sessionSql = @"
                    SELECT id, uid, title, model_name, created_at, updated_at 
                    FROM chat_sessions 
                    WHERE id = @sessionId";

                await using var sessionCommand = new NpgsqlCommand(sessionSql, connection);
                sessionCommand.Parameters.AddWithValue("sessionId", sessionId);

                ChatSession? session = null;
                await using (var reader = await sessionCommand.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        session = new ChatSession
                        {
                            Id = reader.GetString(0),
                            Uid = reader.GetString(1),
                            Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            ModelName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            CreatedAt = reader.GetDateTime(4),
                            UpdatedAt = reader.GetDateTime(5)
                        };
                    }
                }

                if (session == null)
                    return null;

                // 获取消息列表
                var messagesSql = @"
                    SELECT id, session_id, role, content, image_urls, created_at 
                    FROM chat_messages 
                    WHERE session_id = @sessionId 
                    ORDER BY id ASC";

                await using var messagesCommand = new NpgsqlCommand(messagesSql, connection);
                messagesCommand.Parameters.AddWithValue("sessionId", sessionId);

                await using var messagesReader = await messagesCommand.ExecuteReaderAsync();
                while (await messagesReader.ReadAsync())
                {
                    var message = new ChatMessage
                    {
                        Id = messagesReader.GetInt32(0),
                        SessionId = messagesReader.GetString(1),
                        Role = messagesReader.GetString(2),
                        Content = messagesReader.GetString(3),
                        CreatedAt = messagesReader.GetDateTime(5)
                    };

                    // 处理图片URL数组
                    if (!messagesReader.IsDBNull(4))
                    {
                        message.ImageUrls = ((string[])messagesReader.GetValue(4)).ToList();
                    }

                    session.Messages.Add(message);
                }

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取会话详情失败: sessionId={SessionId}", sessionId);
                return null;
            }
        }

        /// <summary>
        /// 保存会话（创建或更新）
        /// </summary>
        public async Task<bool> SaveSessionAsync(SaveSessionRequest request)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();

                await using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    // 检查会话是否存在
                    var checkSql = "SELECT COUNT(*) FROM chat_sessions WHERE id = @id";
                    await using var checkCommand = new NpgsqlCommand(checkSql, connection, transaction);
                    checkCommand.Parameters.AddWithValue("id", request.SessionId);
                    var exists = (long)(await checkCommand.ExecuteScalarAsync() ?? 0) > 0;

                    if (exists)
                    {
                        // 更新会话
                        var updateSql = @"
                            UPDATE chat_sessions 
                            SET title = @title, model_name = @modelName, updated_at = NOW() 
                            WHERE id = @id";
                        await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
                        updateCommand.Parameters.AddWithValue("id", request.SessionId);
                        updateCommand.Parameters.AddWithValue("title", request.Title);
                        updateCommand.Parameters.AddWithValue("modelName", request.ModelName ?? string.Empty);
                        await updateCommand.ExecuteNonQueryAsync();

                        // 删除旧消息
                        var deleteMessagesSql = "DELETE FROM chat_messages WHERE session_id = @sessionId";
                        await using var deleteCommand = new NpgsqlCommand(deleteMessagesSql, connection, transaction);
                        deleteCommand.Parameters.AddWithValue("sessionId", request.SessionId);
                        await deleteCommand.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // 创建新会话
                        var insertSql = @"
                            INSERT INTO chat_sessions (id, uid, title, model_name, created_at, updated_at) 
                            VALUES (@id, @uid, @title, @modelName, NOW(), NOW())";
                        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
                        insertCommand.Parameters.AddWithValue("id", request.SessionId);
                        insertCommand.Parameters.AddWithValue("uid", request.Uid);
                        insertCommand.Parameters.AddWithValue("title", request.Title);
                        insertCommand.Parameters.AddWithValue("modelName", request.ModelName ?? string.Empty);
                        await insertCommand.ExecuteNonQueryAsync();
                    }

                    // 插入消息
                    foreach (var message in request.Messages)
                    {
                        var insertMsgSql = @"
                            INSERT INTO chat_messages (session_id, role, content, image_urls, created_at) 
                            VALUES (@sessionId, @role, @content, @imageUrls, NOW())";
                        await using var msgCommand = new NpgsqlCommand(insertMsgSql, connection, transaction);
                        msgCommand.Parameters.AddWithValue("sessionId", request.SessionId);
                        msgCommand.Parameters.AddWithValue("role", message.Role);
                        msgCommand.Parameters.AddWithValue("content", message.Content);

                        if (message.ImageUrls != null && message.ImageUrls.Count > 0)
                        {
                            msgCommand.Parameters.AddWithValue("imageUrls", message.ImageUrls.ToArray());
                        }
                        else
                        {
                            msgCommand.Parameters.AddWithValue("imageUrls", DBNull.Value);
                        }

                        await msgCommand.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                    _logger.LogInformation("保存会话成功: sessionId={SessionId}", request.SessionId);
                    return true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存会话失败: sessionId={SessionId}", request.SessionId);
                return false;
            }
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();

                // 由于设置了级联删除，删除会话会自动删除关联的消息
                var sql = "DELETE FROM chat_sessions WHERE id = @sessionId";
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("sessionId", sessionId);

                var rowsAffected = await command.ExecuteNonQueryAsync();

                if (rowsAffected > 0)
                {
                    _logger.LogInformation("删除会话成功: sessionId={SessionId}", sessionId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除会话失败: sessionId={SessionId}", sessionId);
                return false;
            }
        }

        /// <summary>
        /// 更新会话标题
        /// </summary>
        public async Task<bool> UpdateSessionTitleAsync(string sessionId, string title)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();

                var sql = "UPDATE chat_sessions SET title = @title, updated_at = NOW() WHERE id = @sessionId";
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("sessionId", sessionId);
                command.Parameters.AddWithValue("title", title);

                var rowsAffected = await command.ExecuteNonQueryAsync();
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新会话标题失败: sessionId={SessionId}", sessionId);
                return false;
            }
        }
    }
}
