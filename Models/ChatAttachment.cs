namespace ChatBot.Models;

public sealed record ChatAttachment(string Id, string Name, string ContentType, long Size);

public sealed class AttachmentOptions
{
    public string StoragePath { get; set; } = "Data/Attachments";
    public string LibreOfficePath { get; set; } = "soffice";
    public string PdfToPpmPath { get; set; } = "pdftoppm";
    public int Workers { get; set; } = 2;
    public int ProcessTimeoutSeconds { get; set; } = 600;
    public int ModelTimeoutSeconds { get; set; } = 300;
    public int ChunkCharacters { get; set; } = 12000;
    public int ContextCharacters { get; set; } = 48000;
}

public sealed record AttachmentJobInput(string[] Attachments, string Model, string Question, string? UserIsolationId = null);
public sealed record AttachmentJob(string Id, string Owner, AttachmentJobInput Input, string Status,
    int Completed, int Total, string Stage, string? Error, string? Result);
public sealed record AttachmentPart(string Label, string Text, string? ImagePath = null);
