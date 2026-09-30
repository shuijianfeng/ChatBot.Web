using ChatBot.Models;
using Npgsql;
using System.Text.Json;

namespace ChatBot.Web.Services;

/// <summary>Durable attachment jobs and checkpoints. Apply the SQL migration before enabling uploads.</summary>
public sealed class AttachmentStore(NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public async Task AddAsync(string owner, ChatAttachment file, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("INSERT INTO chat_attachments(id,owner,metadata) VALUES(@id,@owner,@metadata::jsonb)");
        cmd.Parameters.AddWithValue("id", file.Id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("metadata", Serialize(file));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ChatAttachment> GetAsync(string owner, string id, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT metadata::text FROM chat_attachments WHERE id=@id AND owner=@owner");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        var value = await cmd.ExecuteScalarAsync(ct) as string;
        return value is null ? throw new UnauthorizedAccessException("附件不存在或无权访问。") : JsonSerializer.Deserialize<ChatAttachment>(value, Json)!;
    }

    public async Task<List<ChatAttachment>> ValidateAsync(string owner, IEnumerable<ChatAttachment>? files, CancellationToken ct)
    {
        var refs = files?.ToArray() ?? [];
        if (refs.Length > 5 || refs.Select(f => f.Id).Distinct().Count() != refs.Length)
            throw new InvalidDataException("每条消息最多 5 个不同附件。");
        var result = new List<ChatAttachment>();
        foreach (var file in refs) result.Add(await GetAsync(owner, file.Id, ct));
        if (result.Sum(f => f.Size) > 50L * 1024 * 1024) throw new InvalidDataException("附件总大小不能超过 50MB。");
        return result;
    }

    public async Task<string> CreateJobAsync(string owner, AttachmentJobInput input, CancellationToken ct)
    {
        foreach (var id in input.Attachments) await GetAsync(owner, id, ct);
        var idValue = Guid.NewGuid().ToString("N");
        await using var cmd = database.CreateCommand("INSERT INTO chat_attachment_jobs(id,owner,input) VALUES(@id,@owner,@input::jsonb)");
        cmd.Parameters.AddWithValue("id", idValue);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("input", Serialize(input));
        await cmd.ExecuteNonQueryAsync(ct);
        return idValue;
    }

    public async Task<AttachmentJob?> JobAsync(string owner, string id, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT input::text,status,completed,total,stage,error,result FROM chat_attachment_jobs WHERE id=@id AND owner=@owner");
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("owner", owner);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(id, owner, JsonSerializer.Deserialize<AttachmentJobInput>(reader.GetString(0), Json)!,
            reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task UpdateAsync(string id, string status, int completed, int total, string stage, string? result, string? error, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("""
            UPDATE chat_attachment_jobs SET status=@status,completed=@completed,total=@total,stage=@stage,
            result=@result,error=@error,updated_at=now() WHERE id=@id AND status <> 'cancelled'
            """);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("completed", completed); cmd.Parameters.AddWithValue("total", total);
        cmd.Parameters.AddWithValue("stage", stage);
        cmd.Parameters.AddWithValue("result", (object?)result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ChangeAsync(string owner, string id, bool retry, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand(retry
            ? "UPDATE chat_attachment_jobs SET status='queued',error=NULL,updated_at=now() WHERE id=@id AND owner=@owner AND status IN ('failed','cancelled')"
            : "UPDATE chat_attachment_jobs SET status='cancelled',updated_at=now() WHERE id=@id AND owner=@owner AND status IN ('queued','running')");
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("owner", owner);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> CachedAsync(string owner, string key, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("SELECT result FROM chat_attachment_chunks WHERE owner=@owner AND key=@key");
        cmd.Parameters.AddWithValue("owner", owner); cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task CacheAsync(string owner, string attachmentId, string key, string result, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand("INSERT INTO chat_attachment_chunks(owner,attachment_id,key,result) VALUES(@owner,@file,@key,@result) ON CONFLICT(owner,key) DO NOTHING");
        cmd.Parameters.AddWithValue("owner", owner); cmd.Parameters.AddWithValue("file", attachmentId);
        cmd.Parameters.AddWithValue("key", key); cmd.Parameters.AddWithValue("result", result);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Keep the connection open while holding the session advisory lock. Explicitly release before returning to the pool.
    public async Task<(NpgsqlConnection Connection, AttachmentJob Job)?> ClaimAsync(CancellationToken ct)
    {
        var connection = await database.OpenConnectionAsync(ct);
        try
        {
            var candidates = new List<(string Id, string Owner)>();
            await using (var cmd = new NpgsqlCommand("SELECT id,owner FROM chat_attachment_jobs WHERE status IN ('queued','running') ORDER BY created_at LIMIT 100", connection))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) candidates.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var candidate in candidates)
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id, 719))", connection);
                cmd.Parameters.AddWithValue("id", candidate.Id);
                if (await cmd.ExecuteScalarAsync(ct) is not true) continue;
                var job = await JobAsync(candidate.Owner, candidate.Id, ct);
                if (job?.Status is "queued" or "running") return (connection, job);
                await UnlockAsync(connection, candidate.Id);
            }
        }
        catch { await connection.DisposeAsync(); throw; }
        await connection.DisposeAsync();
        return null;
    }

    public static async Task UnlockAsync(NpgsqlConnection connection, string id)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id,719))", connection);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteScalarAsync();
    }

    public async Task<List<string>> ExpireAsync(CancellationToken ct)
    {
        // A saved reference wins over expiration, even if it was added after uploading.
        await using var cmd = database.CreateCommand("""
            DELETE FROM chat_attachments a WHERE a.created_at < now()-interval '24 hours'
            AND NOT EXISTS(SELECT 1 FROM chat_messages m, jsonb_array_elements(m.attachments) r WHERE r->>'id'=a.id)
            AND NOT EXISTS(SELECT 1 FROM chat_attachment_jobs j WHERE j.status IN ('queued','running') AND j.input->'attachments' ? a.id)
            RETURNING a.id
            """);
        var ids = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        if (ids.Count > 0)
        {
            await using var jobs = database.CreateCommand("DELETE FROM chat_attachment_jobs WHERE input->'attachments' ?| @ids");
            jobs.Parameters.AddWithValue("ids", ids.ToArray());
            await jobs.ExecuteNonQueryAsync(ct);
        }
        return ids;
    }
}
