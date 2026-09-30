using ChatBot.Models;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace ChatBot.Web.Services;

public sealed partial class AttachmentExtractor
{
    private static bool IsTextAttachment(string contentType) => contentType is
        "text/plain" or "text/markdown" or "text/csv" or "application/xml" or "application/json" or "text/html";
    private static bool IsLegacyOffice(string contentType) => contentType is
        "application/msword" or "application/vnd.ms-excel" or "application/vnd.ms-works";

    internal static string ReadTextAttachment(string path, string contentType)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            // Common Chinese CSV/HTML/XML exports use GBK or GB18030 without a BOM.
            try { text = File.ReadAllText(path, Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("文本编码无法识别，请另存为 UTF-8 后上传。", ex); }
        }
        if (text.Any(c => c == '\0' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))))
            throw new InvalidDataException("文件含有二进制内容，请上传有效的文本文件。");
        try
        {
            if (contentType == "application/json")
            {
                using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
            }
            if (contentType == "application/xml")
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50L * 1024 * 1024 });
                while (reader.Read()) { }
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("JSON 格式无效，请检查语法。", ex); }
        catch (XmlException ex) { throw new InvalidDataException("XML 格式无效或含有不允许的 DTD/外部实体。", ex); }
        // Preserve quoted multiline CSV fields, JSON keys/types and XML/HTML structure verbatim.
        // HTML is data only: never execute scripts, resolve links, or fetch external resources.
        return text;
    }

    private static void ValidateLegacyOffice(string path, string contentType)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        int length = stream.Read(header);
        bool compound = length == 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
        bool zip = length >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 3 && header[3] == 4;
        bool rtf = length >= 5 && Encoding.ASCII.GetString(header[..5]) == "{\\rtf";
        if (!compound && !zip && !(rtf && contentType != "application/vnd.ms-excel"))
            throw new InvalidDataException("文件不是支持的 Office/WPS 文档，或已损坏；请另存为 DOCX/XLSX 后重试。");
        if (zip)
            ValidateFile(path, contentType == "application/vnd.ms-excel"
                ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }

    private async Task<IReadOnlyList<AttachmentPart>> ExtractLegacyOfficeAsync(ChatAttachment file, CancellationToken ct)
    {
        var targetExtension = file.ContentType == "application/vnd.ms-excel" ? ".xlsx" : ".docx";
        var normalized = file with { Name = Path.ChangeExtension(file.Name, targetExtension), ContentType = ValidateExtension("file" + targetExtension) };
        // Release the converter gate before re-entering extraction (complex documents may need it again).
        await ConversionGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(Source(normalized)))
            {
                var directory = DirectoryFor(file.Id);
                var profile = Path.Combine(directory, "legacy-office-profile");
                Directory.CreateDirectory(Path.Combine(profile, "user"));
                await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"), """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <oor:items xmlns:oor="http://openoffice.org/2001/registry">
                      <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
                    </oor:items>
                    """, ct);
                // Publish the normalized file only after a successful, validated conversion.
                var output = Path.Combine(directory, "normalize-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(output);
                var filter = targetExtension == ".xlsx" ? "xlsx:Calc MS Excel 2007 XML" : "docx:Office Open XML Text";
                await RunAsync(settings.LibreOfficePath,
                    [$"-env:UserInstallation={new Uri(profile).AbsoluteUri}", "--headless", "--convert-to", filter, "--outdir", output, Source(file)], ct);
                var converted = Path.Combine(output, "source" + targetExtension);
                if (!File.Exists(converted)) throw new InvalidDataException("Office/WPS 转换失败，文件可能加密或版本不受支持，请另存为 DOCX/XLSX。");
                ValidateFile(converted, normalized.ContentType);
                File.Move(converted, Source(normalized), true);
            }
        }
        finally { ConversionGate.Release(); }
        var parts = await ExtractAsync(normalized, ct);
        return parts.Select(part => part with {
            Label = part.Label.StartsWith(normalized.Name, StringComparison.Ordinal)
                ? file.Name + part.Label[normalized.Name.Length..] : part.Label
        }).ToArray();
    }
}
