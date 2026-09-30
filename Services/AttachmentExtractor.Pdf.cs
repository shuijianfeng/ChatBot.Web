using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace ChatBot.Web.Services;

public sealed partial class AttachmentExtractor
{
    private static bool PdfNeedsVisual(PdfPage page, string text, CancellationToken ct)
    {
        // A text layer alone does not prove a page is complete: scanned pages can have OCR overlays.
        if (text.Count(char.IsLetterOrDigit) < 20 || text.Any(c => c == '\ufffd' || c == '\0') || page.GetAnnotations().Count > 0)
            return true;
        if (HasSpecialPdfResources(page.GetResources().GetPdfObject(), new HashSet<PdfDictionary>())) return true;
        var listener = new PdfVisualListener(ct);
        new PdfCanvasProcessor(listener).ProcessPageContent(page);
        return listener.HasVisual || !listener.RectanglesFormTable();
    }

    private static bool HasSpecialPdfResources(PdfDictionary resources, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(resources)) return false;
        if (resources.ContainsKey(PdfName.Shading) || resources.ContainsKey(PdfName.Pattern)) return true;
        if (resources.GetAsDictionary(PdfName.Font) is { } fonts)
            foreach (var key in fonts.KeySet())
                if (PdfName.Type3.Equals(fonts.GetAsDictionary(key)?.GetAsName(PdfName.Subtype))) return true;
        if (resources.GetAsDictionary(PdfName.XObject) is { } objects)
            foreach (var key in objects.KeySet())
                if (objects.GetAsStream(key)?.GetAsDictionary(PdfName.Resources) is { } nested && HasSpecialPdfResources(nested, visited)) return true;
        return false;
    }

    private sealed class PdfVisualListener(CancellationToken ct) : IEventListener
    {
        public bool HasVisual { get; private set; }
        private readonly List<(double Left, double Bottom, double Right, double Top)> rectangles = [];
        public bool RectanglesFormTable()
        {
            // Cell outlines share complete sides. Isolated outlined shapes/bar charts do not.
            if (rectangles.Count == 0) return true;
            return rectangles.Count > 1 && rectangles.All(a => rectangles.Any(b =>
            {
                if (a == b) return false;
                bool vertical = (Near(a.Right,b.Left) || Near(a.Left,b.Right)) && Near(a.Bottom,b.Bottom) && Near(a.Top,b.Top);
                bool horizontal = (Near(a.Top,b.Bottom) || Near(a.Bottom,b.Top)) && Near(a.Left,b.Left) && Near(a.Right,b.Right);
                return vertical || horizontal;
            }));
        }
        private static bool Near(double a, double b) => Math.Abs(a-b)<.1;
        public void EventOccurred(IEventData data, EventType type)
        {
            ct.ThrowIfCancellationRequested();
            if (type == EventType.RENDER_IMAGE) { HasVisual = true; return; }
            if (data is not PathRenderInfo path || path.GetOperation() == PathRenderInfo.NO_OP) return;
            // Only plain horizontal/vertical strokes (table rules) can be omitted from OCR.
            // Filled regions, curves, diagonal strokes and polygons keep full visual recognition.
            if (path.GetOperation() != PathRenderInfo.STROKE) { HasVisual = true; return; }
            var matrix = path.GetCtm();
            if (Math.Abs(matrix.Get(Matrix.I12)) > .001 || Math.Abs(matrix.Get(Matrix.I21)) > .001)
            { HasVisual = true; return; }
            foreach (var subpath in path.GetPath().GetSubpaths())
            {
                if (subpath.GetSegments().Count > 1 || subpath.IsClosed())
                {
                    var segments = subpath.GetSegments();
                    if (!subpath.IsClosed() || segments.Count is < 3 or > 4 || segments.Any(s => s is not Line))
                    { HasVisual = true; return; }
                    var points = segments.SelectMany(s => s.GetBasePoints()).ToArray();
                    var left = points.Min(p => p.GetX()); var right = points.Max(p => p.GetX());
                    var bottom = points.Min(p => p.GetY()); var top = points.Max(p => p.GetY());
                    if (points.Any(p => !(Near(p.GetX(),left) || Near(p.GetX(),right)) || !(Near(p.GetY(),bottom) || Near(p.GetY(),top))))
                    { HasVisual = true; return; }
                    rectangles.Add((left,bottom,right,top));
                }
                foreach (var segment in subpath.GetSegments())
                {
                    if (segment is not Line) { HasVisual = true; return; }
                    var points = segment.GetBasePoints();
                    if (points.Count != 2 || (Math.Abs(points[0].GetX() - points[1].GetX()) > .01 && Math.Abs(points[0].GetY() - points[1].GetY()) > .01))
                    { HasVisual = true; return; }
                }
            }
        }
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_IMAGE, EventType.RENDER_PATH };
    }
}
