namespace ChatBot.Models;

/// <summary>供应商配置仅在服务器读取，不作为接口响应返回。</summary>
public sealed class EmbeddingConfig
{
    public bool Enabled { get; set; }
    public string ChatModelName { get; set; } = "";
    public string ApiEndpoint { get; set; } = "";
    public string EnvironmentApikeyName { get; set; } = "";
    public string Model { get; set; } = "";
    public int? Dimensions { get; set; }
    public string? QueryInstruction { get; set; }
    public bool SendEncodingFormat { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed record EmbeddingConfiguration(string Id, int BatchSize, int MaxTextLength,
    int? Dimensions, string? QueryInstruction);
public sealed record EmbeddingRequest(string ConfigurationId, string[] Input);
public sealed record EmbeddingVector(int Index, double[] Embedding);
public sealed record EmbeddingResponse(string ConfigurationId, EmbeddingVector[] Data);
