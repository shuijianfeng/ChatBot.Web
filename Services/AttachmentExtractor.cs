using ChatBot.Models;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ChatBot.Web.Services;

/// <summary>Extracts all source text and bounded, legible page tiles without invoking Office macros.</summary>
public sealed partial class AttachmentExtractor(IWebHostEnvironment env, IOptions<AttachmentOptions> options)
{
    private readonly AttachmentOptions settings = options.Value;
    private static readonly SemaphoreSlim ConversionGate = new(1);
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    public string Root
    {
        get
        {
            var root = Path.GetFullPath(settings.StoragePath, env.ContentRootPath);
            var web = Path.GetFullPath(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"));
            if (root.Equals(web, StringComparison.OrdinalIgnoreCase) || root.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("附件存储必须位于静态资源目录之外。");
            return root;
        }
    }

    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("附件标识无效。");
        return Path.Combine(Root, id);
    }
    public string Source(ChatAttachment file) => Path.Combine(DirectoryFor(file.Id), "source" + Path.GetExtension(file.Name).ToLowerInvariant());

    public static string ValidateExtension(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".doc" => "application/msword", ".xls" => "application/vnd.ms-excel", ".wps" => "application/vnd.ms-works",
        ".txt" => "text/plain", ".md" => "text/markdown",
        ".csv" => "text/csv", ".xml" => "application/xml", ".json" => "application/json", ".html" or ".htm" => "text/html",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp", ".gif" => "image/gif", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff",
        _ => throw new InvalidDataException("支持 DOC/DOCX、XLS/XLSX、WPS、TXT、MD、CSV、XML、JSON、HTML、PDF 和 PNG/JPEG/WebP/GIF/BMP/TIFF 图片。")
    };

    public static void ValidateFile(string path, string contentType)
    {
        if (IsTextAttachment(contentType)) { ReadTextAttachment(path, contentType); return; }
        if (IsLegacyOffice(contentType)) { ValidateLegacyOffice(path, contentType); return; }
        if (contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            var info = Image.Identify(path);
            if (info is null || (long)info.Width * info.Height > 100_000_000)
                throw new InvalidDataException("图片无效或像素过大（最多一亿像素）。");
            return;
        }
        if (contentType == "application/pdf")
        {
            using var reader = new PdfReader(path);
            using var pdf = new PdfDocument(reader);
            if (reader.IsEncrypted()) throw new InvalidDataException("请解除 PDF 密码后重新上传。");
            if (pdf.GetNumberOfPages() == 0) throw new InvalidDataException("PDF 没有页面。");
            return;
        }
        using var zip = ZipFile.OpenRead(path);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 1024L * 1024 * 1024 || zip.Entries.Count > 100000)
                throw new InvalidDataException("文档解压后过大，请拆分文档后上传。");
        }
        var required = contentType.Contains("wordprocessingml") ? "word/document.xml" : "xl/workbook.xml";
        if (zip.GetEntry("[Content_Types].xml") is null || zip.GetEntry(required) is null)
            throw new InvalidDataException("文件内容与扩展名不符，或文档已损坏/加密。");
    }

    public static IEnumerable<string> SplitText(string text, int limit)
    {
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit));
        for (int offset = 0; offset < text.Length;)
        {
            int length = Math.Min(limit, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    public async Task<IReadOnlyList<AttachmentPart>> ExtractAsync(ChatAttachment file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsTextAttachment(file.ContentType))
        {
            var text = ReadTextAttachment(Source(file), file.ContentType);
            ct.ThrowIfCancellationRequested();
            return SplitText(text, settings.ChunkCharacters)
                .Select((part, index) => new AttachmentPart($"{file.Name} 原文块 {index + 1}", part)).ToArray();
        }
        if (IsLegacyOffice(file.ContentType)) return await ExtractLegacyOfficeAsync(file, ct);
        // Plain workbooks need no conversion or OCR, and must not wait behind Office jobs.
        // Bypass old manifests too: they may contain redundant whole-sheet screenshots.
        if (Path.GetExtension(file.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !WorkbookNeedsRendering(Source(file), ct))
            return ReadOffice(Source(file), ".xlsx", settings.ChunkCharacters, ct)
                .Select(part => part with { Label = file.Name + " " + part.Label }).ToArray();
        if (Path.GetExtension(file.Name).Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
            ReadWordPictures(Source(file), ct) is { } wordPictures)
            return await ExtractWordAsync(file, wordPictures, ct);
        await ConversionGate.WaitAsync(ct);
        try
        {
            var directory = DirectoryFor(file.Id);
            var manifest = Path.Combine(directory, file.ContentType == "application/pdf" ? "parts-pdf-v2.json" : "parts-v1.json");
            if (File.Exists(manifest)) return JsonSerializer.Deserialize<List<AttachmentPart>>(await File.ReadAllTextAsync(manifest, ct))!;
            var parts = new List<AttachmentPart>();
            var source = Source(file);
            if (file.ContentType.StartsWith("image/", StringComparison.Ordinal))
            {
                using var image = await Image.LoadAsync(source, ct);
                image.Mutate(x => x.AutoOrient());
                // Preserve every frame/page of animated GIF and multi-page TIFF.
                for (int frame = 0; frame < image.Frames.Count; frame++)
                using (var page = image.Frames.CloneFrame(frame))
                {
                    int tile = 0;
                    for (int y = 0; y < page.Height; y += 1520)
                    for (int x = 0; x < page.Width; x += 1520)
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = Path.Combine(directory, $"image-{frame}-{tile++}.png");
                        using var crop = page.Clone(p => p.Crop(new Rectangle(x, y, Math.Min(1600, page.Width - x), Math.Min(1600, page.Height - y))));
                        await crop.SaveAsPngAsync(path, ct);
                        parts.Add(new($"{file.Name} 第 {frame + 1} 帧 x={x} y={y}", "", path));
                    }
                }
            }
            else
            {
                var extension = Path.GetExtension(file.Name).ToLowerInvariant();
                string pdfPath = source;
                if (extension is ".docx" or ".xlsx")
                {
                    foreach (var part in ReadOffice(source, extension, settings.ChunkCharacters, ct))
                        parts.Add(part with { Label = file.Name + " " + part.Label });
                    // Never share a LibreOffice user profile between conversions.
                    var profile = Path.Combine(directory, "office-profile");
                    var filter = extension == ".xlsx"
                        ? "pdf:calc_pdf_Export:{\"SinglePageSheets\":{\"type\":\"boolean\",\"value\":\"true\"}}"
                        : "pdf:writer_pdf_Export";
                    await RunAsync(settings.LibreOfficePath,
                        [$"-env:UserInstallation={new Uri(profile).AbsoluteUri}", "--headless", "--convert-to", filter, "--outdir", directory, source], ct);
                    pdfPath = Path.Combine(directory, "source.pdf");
                    if (!File.Exists(pdfPath)) throw new InvalidDataException("Office 文档转换失败，请检查 LibreOffice 和字体配置。");
                }
                using var reader = new PdfReader(pdfPath);
                using var pdf = new PdfDocument(reader);
                for (int page = 1; page <= pdf.GetNumberOfPages(); page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var label = $"{file.Name} 第 {page} 页";
                    var text = PdfTextExtractor.GetTextFromPage(pdf.GetPage(page));
                    int chunk = 0;
                    foreach (var section in SplitText(text, settings.ChunkCharacters))
                        parts.Add(new($"{label} 文字块 {++chunk}", section));
                    if (extension == ".pdf" && !PdfNeedsVisual(pdf.GetPage(page), text, ct)) continue;
                    var size = pdf.GetPage(page).GetPageSizeWithRotation();
                    var width = checked((int)Math.Ceiling(size.GetWidth() * 2));
                    var height = checked((int)Math.Ceiling(size.GetHeight() * 2));
                    // Render tiles directly; never allocate an entire enormous spreadsheet page.
                    for (int y = 0; y < height; y += 1520)
                    for (int x = 0; x < width; x += 1520)
                    {
                        var prefix = Path.Combine(directory, $"page-{page}-{x}-{y}");
                        if (!File.Exists(prefix + ".png"))
                            await RunAsync(settings.PdfToPpmPath,
                                ["-f", page.ToString(), "-l", page.ToString(), "-r", "144", "-x", x.ToString(), "-y", y.ToString(),
                                 "-W", Math.Min(1600, width - x).ToString(), "-H", Math.Min(1600, height - y).ToString(),
                                 "-singlefile", "-png", pdfPath, prefix], ct);
                        if (!File.Exists(prefix + ".png")) throw new InvalidDataException("PDF 页面渲染失败。");
                        parts.Add(new($"{label} 区域 x={x} y={y}", "", prefix + ".png"));
                    }
                }
            }
            await File.WriteAllTextAsync(manifest + ".tmp", JsonSerializer.Serialize(parts), ct);
            File.Move(manifest + ".tmp", manifest, true);
            return parts;
        }
        finally { ConversionGate.Release(); }
    }

    public static IEnumerable<AttachmentPart> ReadOffice(string path, string extension, int chunkSize, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(path);
        if (extension == ".docx")
        {
            foreach (var entry in zip.Entries.Where(e => IsWordTextPart(e.FullName)))
            {
                if (!entry.FullName.EndsWith(".xml", StringComparison.Ordinal)) continue;
                using var stream = entry.Open();
                var document = XDocument.Load(stream);
                var text = WordText(document, WordNumbering(zip), ct);
                int chunk = 0;
                foreach (var section in SplitText(text, chunkSize)) yield return new($"{entry.Name} 文字块 {++chunk}", section);
            }
            yield break;
        }
        XDocument Read(string name)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException("工作簿关系无效。")).Open();
            return XDocument.Load(stream);
        }
        var styles = zip.GetEntry("xl/styles.xml") is null ? null : Read("xl/styles.xml");
        var formats = styles?.Descendants(S + "numFmt").ToDictionary(
            e => (int)e.Attribute("numFmtId")!, e => (string)e.Attribute("formatCode")!) ?? new Dictionary<int, string>();
        var cellFormats = styles?.Root?.Element(S + "cellXfs")?.Elements(S + "xf")
            .Select(e => (int?)e.Attribute("numFmtId") ?? 0).ToArray() ?? [];
        var dateSystem = (string?)Read("xl/workbook.xml").Root?.Element(S + "workbookPr")?.Attribute("date1904");
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Read("xl/sharedStrings.xml").Descendants(S + "si")
            .Select(e => string.Concat(e.Descendants(S + "t").Select(t => t.Value))).ToArray();
        var relations = Read("xl/_rels/workbook.xml.rels").Descendants(P + "Relationship")
            .Where(e => (string?)e.Attribute("TargetMode") != "External")
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        foreach (var sheet in Read("xl/workbook.xml").Descendants(S + "sheet"))
        {
            var target = relations[(string)sheet.Attribute(R + "id")!];
            var uri = new Uri(new Uri("https://package/xl/workbook.xml"), target);
            var document = Read(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
            var label = $"工作表 {sheet.Attribute("name")?.Value}（{sheet.Attribute("state")?.Value ?? "visible"}）";
            var buffer = new StringBuilder();
            int chunk = 0;
            foreach (var cell in document.Descendants(S + "c"))
            {
                ct.ThrowIfCancellationRequested();
                var value = cell.Element(S + "v")?.Value ?? string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
                if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out int index)) value = shared[index];
                var formula = cell.Element(S + "f")?.Value;
                if (value.Length == 0 && formula is null) continue; // Ignore style-only cells, never zero or formulas.
                var format = "";
                var styleIndex = (int?)cell.Attribute("s") ?? 0;
                if (styleIndex >= 0 && styleIndex < cellFormats.Length)
                {
                    var id = cellFormats[styleIndex];
                    if (id != 0) format = "\t格式=" + (formats.TryGetValue(id, out var code) ? code : BuiltinFormat(id))
                        + (dateSystem is "1" or "true" ? "（1904日期系统）" : "");
                }
                var line = $"{cell.Attribute("r")?.Value}\t{value}" + (formula is null ? "" : $"\t公式={formula}") + format + "\n";
                foreach (var piece in SplitText(line, chunkSize))
                {
                    if (buffer.Length + piece.Length > chunkSize)
                    { yield return new($"{label} 数据块 {++chunk}", buffer.ToString()); buffer.Clear(); }
                    buffer.Append(piece);
                }
            }
            foreach (var merge in document.Descendants(S + "mergeCell"))
            {
                var line = "合并单元格=" + merge.Attribute("ref")?.Value + "\n";
                if (buffer.Length + line.Length > chunkSize)
                { yield return new($"{label} 数据块 {++chunk}", buffer.ToString()); buffer.Clear(); }
                buffer.Append(line);
            }
            if (buffer.Length > 0) yield return new($"{label} 数据块 {++chunk}", buffer.ToString());
        }
    }

    private sealed record WordNumber(int Start, string Template, string Suffix);
    private static Dictionary<string, WordNumber>? WordNumbering(ZipArchive zip)
    {
        XDocument? Read(string path)
        {
            if (zip.GetEntry(path) is not { } entry) return null;
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        var document = Read("word/numbering.xml");
        var result = new Dictionary<string, WordNumber>();
        if (document is null) return result;
        foreach (var num in document.Descendants(W + "num"))
        {
            var abstractId = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val");
            var definition = document.Descendants(W + "abstractNum").FirstOrDefault(e => (string?)e.Attribute(W + "abstractNumId") == abstractId);
            var levels = definition?.Elements(W + "lvl").ToArray() ?? [];
            if (levels.Length != 1 || (string?)levels[0].Attribute(W + "ilvl") != "0" ||
                (string?)levels[0].Element(W + "numFmt")?.Attribute(W + "val") != "decimal" ||
                num.Elements(W + "lvlOverride").Any() || definition!.Element(W + "numStyleLink") is not null) continue;
            var level = levels[0];
            var template = (string?)level.Element(W + "lvlText")?.Attribute(W + "val") ?? "%1.";
            if (System.Text.RegularExpressions.Regex.IsMatch(template, "%[2-9]")) continue;
            var suffix = (string?)level.Element(W + "suff")?.Attribute(W + "val") switch { "nothing" => "", "space" => " ", _ => "\t" };
            result[(string)num.Attribute(W + "numId")!] = new((int?)level.Element(W + "start")?.Attribute(W + "val") ?? 1, template, suffix);
        }
        return result;
    }


    private sealed record WordPicture(string Part, string Target, int Paragraph);

    private static bool IsWordTextPart(string name) => name == "word/document.xml" ||
        ((name.StartsWith("word/header", StringComparison.Ordinal) || name.StartsWith("word/footer", StringComparison.Ordinal)) && name.EndsWith(".xml", StringComparison.Ordinal)) ||
        name is "word/footnotes.xml" or "word/endnotes.xml";

    // Only ordinary raster pictures are handled natively. Charts, shapes, equations and
    // embedded objects keep the existing rendering path so their visual meaning survives.
    private static List<WordPicture>? ReadWordPictures(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Any(e => new[] { "word/charts/", "word/diagrams/", "word/embeddings/" }
            .Any(prefix => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))) return null;
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var numbering = WordNumbering(zip);
        if (numbering is null) return null;
        var numberedStyles = new HashSet<string>();
        if (zip.GetEntry("word/styles.xml") is { } stylesEntry)
        {
            using var stylesStream = stylesEntry.Open();
            var styles = XDocument.Load(stylesStream).Descendants(W + "style").ToArray();
            foreach (var style in styles.Where(e => e.Descendants(W + "numPr").Any()))
                numberedStyles.Add((string?)style.Attribute(W + "styleId") ?? "");
            bool changed;
            do
            {
                changed = false;
                foreach (var style in styles)
                    if (numberedStyles.Contains((string?)style.Element(W + "basedOn")?.Attribute(W + "val") ?? ""))
                        changed |= numberedStyles.Add((string?)style.Attribute(W + "styleId") ?? "");
            } while (changed);
            if (styles.Any(e => (string?)e.Attribute(W + "default") is "1" or "true" &&
                numberedStyles.Contains((string?)e.Attribute(W + "styleId") ?? ""))) return null;
        }
        var pictures = new List<WordPicture>();
        foreach (var entry in zip.Entries.Where(e => IsWordTextPart(e.FullName)))
        {
            ct.ThrowIfCancellationRequested();
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            if (document.Descendants().Any(e => e.Name == W + "pict" || e.Name == W + "object" ||
                e.Name == W + "altChunk" || e.Name == W + "txbxContent" || e.Name == W + "sym" ||
                e.Name.NamespaceName == "http://schemas.openxmlformats.org/officeDocument/2006/math")) return null;
            var relPath = entry.FullName[..(entry.FullName.LastIndexOf('/') + 1)] + "_rels/" + entry.Name + ".rels";
            var relations = new Dictionary<string, XElement>();
            if (zip.GetEntry(relPath) is { } relEntry)
            {
                using var relStream = relEntry.Open();
                relations = XDocument.Load(relStream).Descendants(P + "Relationship")
                    .ToDictionary(e => (string)e.Attribute("Id")!, e => e);
            }
            foreach (var num in document.Descendants(W + "numPr"))
            {
                var id = (string?)num.Element(W + "numId")?.Attribute(W + "val");
                if (((string?)num.Element(W + "ilvl")?.Attribute(W + "val") is { } level && level != "0") ||
                    (id is not null && id != "0" && !numbering.ContainsKey(id))) return null;
            }
            var paragraphs = document.Descendants(W + "p").ToArray();
            if (paragraphs.Any(p => numberedStyles.Contains((string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? ""))) return null;
            foreach (var drawing in document.Descendants(W + "drawing"))
            {
                ct.ThrowIfCancellationRequested();
                var graphics = drawing.Descendants(a + "graphicData").ToArray();
                var blips = drawing.Descendants(a + "blip").ToArray();
                if (graphics.Length != 1 || (string?)graphics[0].Attribute("uri") != "http://schemas.openxmlformats.org/drawingml/2006/picture" ||
                    blips.Length != 1 || blips[0].Attribute(R + "link") is not null) return null;
                var id = (string?)blips[0].Attribute(R + "embed");
                if (id is null || !relations.TryGetValue(id, out var relation) || (string?)relation.Attribute("TargetMode") == "External") return null;
                var target = (string?)relation.Attribute("Target");
                if (string.IsNullOrWhiteSpace(target)) return null;
                var uri = new Uri(new Uri("https://package/" + entry.FullName), target);
                if (uri.Host != "package") return null;
                var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                if (zip.GetEntry(name) is null || Path.GetExtension(name).ToLowerInvariant() is not
                    (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp")) return null;
                // SVG alternatives and complex picture transforms/effects require the rendered appearance.
                if (drawing.Descendants().Any(e => e.Name.LocalName is "svgBlip" or "srcRect" or "effectLst" or "effectDag") ||
                    drawing.Descendants(a + "xfrm").Any(e => e.Attribute("rot") is not null || e.Attribute("flipH") is not null || e.Attribute("flipV") is not null)) return null;
                pictures.Add(new(entry.Name, name, Array.IndexOf(paragraphs, drawing.Ancestors(W + "p").FirstOrDefault()) + 1));
            }
        }
        return pictures;
    }

    private async Task<IReadOnlyList<AttachmentPart>> ExtractWordAsync(ChatAttachment file, List<WordPicture> pictures, CancellationToken ct)
    {
        var parts = ReadOffice(Source(file), ".docx", settings.ChunkCharacters, ct)
            .Select(part => part with { Label = file.Name + " " + part.Label }).ToList();
        using var zip = ZipFile.OpenRead(Source(file));
        // A repeated logo needs OCR once, while every occurrence retains its source location.
        foreach (var group in pictures.GroupBy(p => p.Target))
        {
            ct.ThrowIfCancellationRequested();
            using (var infoStream = zip.GetEntry(group.Key)!.Open())
            {
                var info = await Image.IdentifyAsync(infoStream, ct);
                if (info is null || (long)info.Width * info.Height > 100_000_000)
                    throw new InvalidDataException("嵌入图片无效或像素过大（最多一亿像素）。");
            }
            using var stream = zip.GetEntry(group.Key)!.Open();
            using var image = await Image.LoadAsync(stream, ct);
            image.Mutate(x => x.AutoOrient());
            var label = file.Name + " 嵌入图片（" + string.Join("；", group.Select(p => $"{p.Part} 段落 {p.Paragraph}")) + "）";
            var imageId = Guid.NewGuid().ToString("N");
            for (int frame = 0; frame < image.Frames.Count; frame++)
            using (var page = image.Frames.CloneFrame(frame))
            {
                for (int y = 0; y < page.Height; y += 1520)
                for (int x = 0; x < page.Width; x += 1520)
                {
                    ct.ThrowIfCancellationRequested();
                    var output = Path.Combine(DirectoryFor(file.Id), $"word-image-{imageId}-{frame}-{x}-{y}.png");
                    using var crop = page.Clone(p => p.Crop(new Rectangle(x, y, Math.Min(1600, page.Width - x), Math.Min(1600, page.Height - y))));
                    await crop.SaveAsPngAsync(output, ct);
                    parts.Add(new($"{label} 第 {frame + 1} 帧 x={x} y={y}", "", output));
                }
            }
        }
        return parts;
    }

    private static string WordText(XDocument document, Dictionary<string, WordNumber>? numbering, CancellationToken ct)
    {
        var text = new StringBuilder();
        int tableIndex = 0, paragraphIndex = 0;
        var numbers = new Dictionary<string, int>();
        foreach (var element in document.Descendants())
        {
            ct.ThrowIfCancellationRequested();
            if (element.Name == W + "tbl") text.Append($"\n[表格 {++tableIndex}]\n");
            if (element.Name == W + "tr") text.Append("\n[行]\t");
            if (element.Name == W + "tc")
            {
                var properties = element.Element(W + "tcPr");
                text.Append("[单元格");
                if (properties?.Element(W + "gridSpan")?.Attribute(W + "val") is { } span) text.Append($" 跨{span.Value}列");
                if (properties?.Element(W + "vMerge") is { } merge) text.Append(" 纵向合并=" + ((string?)merge.Attribute(W + "val") ?? "continue"));
                text.Append("]\t");
            }
            if (element.Name == W + "p")
            {
                paragraphIndex++;
                if (text.Length > 0) text.Append('\n');
            }
            if (element.Name == W + "drawing") text.Append($"[嵌入图片：段落 {paragraphIndex}]");
            if (element.Name == W + "p" && numbering is not null &&
                (string?)element.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val") is { } numId &&
                numbering.TryGetValue(numId, out var number))
            {
                var value = numbers.TryGetValue(numId, out var previous) ? previous + 1 : number.Start;
                numbers[numId] = value;
                text.Append(number.Template.Replace("%1", value.ToString(System.Globalization.CultureInfo.InvariantCulture))).Append(number.Suffix);
            }
            // Iterate text nodes once: nested textboxes must not duplicate their paragraphs.
            if (element.Name == W + "t") text.Append(element.Value);
            else if (element.Name == W + "tab") text.Append('\t');
            else if (element.Name == W + "br" || element.Name == W + "cr") text.Append('\n');
        }
        return text.ToString();
    }


    private static string BuiltinFormat(int id) => id switch
    {
        1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00", 9 => "0%", 10 => "0.00%",
        11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??", 14 => "mm-dd-yy", 15 => "d-mmm-yy",
        16 => "d-mmm", 17 => "mmm-yy", 18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM",
        20 => "h:mm", 21 => "h:mm:ss", 22 => "m/d/yy h:mm", 45 => "mm:ss", 46 => "[h]:mm:ss",
        47 => "mmss.0", 48 => "##0.0E+0", 49 => "@", _ => "Excel内置格式 " + id
    };

    private static bool WorkbookNeedsRendering(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (new[] { "xl/drawings/", "xl/charts/", "xl/chartsheets/", "xl/media/", "xl/embeddings/", "xl/richData/" }
                .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                name.Equals("xl/cellimages.xml", StringComparison.OrdinalIgnoreCase)) return true;
            if (!name.StartsWith("xl/worksheets/", StringComparison.Ordinal) || !name.EndsWith(".xml", StringComparison.Ordinal)) continue;
            using var stream = entry.Open();
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName is
                    "drawing" or "legacyDrawing" or "legacyDrawingHF" or "picture" or "oleObjects" or "controls" or "sparklineGroups" or "headerFooter") return true;
            }
        }
        return false;
    }

    private async Task RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ProcessTimeoutSeconds));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动文档转换工具。");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidDataException("文档转换工具失败，请检查文件及服务器组件。");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}
