using System.Globalization;
using System.Text;
using RealEstate.Application.Payments;
using RealEstate.Domain.Enums;

namespace RealEstate.Infrastructure.Documents;

/// <summary>Renders payment receipts as A4 PDF documents without third-party libraries.</summary>
public sealed class ReceiptPdfRenderer : IReceiptPdfRenderer
{
    private const double PageWidth = 595, PageHeight = 842, Margin = 40, Bottom = 70;
    private const double ContentWidth = PageWidth - 2 * Margin;

    public byte[] Render(ReceiptDocument r)
    {
        var pdf = new SimplePdfWriter(PageWidth, PageHeight);
        pdf.AddPage();
        var L = new Cursor(pdf) { Y = PageHeight - Margin };

        // ---- header ----
        pdf.Text(Margin, L.Y - 16, r.CompanyName, 16, true);
        L.Y -= 22;
        foreach (var line in SimplePdfWriter.Wrap(r.CompanyAddress ?? "", 10, ContentWidth * 0.6, false)) { pdf.Text(Margin, L.Y - 10, line, 10, false); L.Y -= 13; }
        if (!string.IsNullOrWhiteSpace(r.CompanyPhone)) { pdf.Text(Margin, L.Y - 10, "Phone: " + r.CompanyPhone, 10, false); L.Y -= 13; }
        L.Y -= 6;
        pdf.Line(Margin, L.Y, PageWidth - Margin, L.Y, 1);
        L.Y -= 10;

        // ---- title and copy label ----
        pdf.Text(Margin, L.Y - 20, "PAYMENT RECEIPT", 20, true);
        var duplicate = r.CopyNumber > 1;
        var label = r.CopyLabel;
        var labelWidth = Math.Max(130, SimplePdfWriter.Width(label, 14, true) + 24);
        var lx = PageWidth - Margin - labelWidth;
        if (duplicate) pdf.Rect(lx, L.Y - 30, labelWidth, 28, fillGray: 0.15, strokeWidth: 0);
        else pdf.Rect(lx, L.Y - 30, labelWidth, 28, fillGray: null, strokeWidth: 1.5);
        pdf.Text(lx + 12, L.Y - 22, label, 14, true, whiteText: duplicate);
        L.Y -= 42;

        if (r.Status == PaymentStatus.Reversed)
        {
            pdf.Rect(Margin, L.Y - 26, ContentWidth, 26, fillGray: null, strokeWidth: 0, fillRgb: (0.75, 0.1, 0.1));
            pdf.Text(Margin + 12, L.Y - 19, "REVERSED - this payment has been reversed and is no longer valid", 12, true, whiteText: true);
            L.Y -= 36;
        }

        // ---- details ----
        var money = r.CurrencySymbol + " " + r.Amount.ToString("N2", CultureInfo.InvariantCulture);
        var rows = new List<(string, string)>
        {
            ("Receipt no", r.ReceiptNumber), ("Payment no", r.PaymentNumber), ("Payment date", r.PaymentDate.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)),
            ("Issued at", r.IssuedAtUtc.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC"),
            ("Customer", $"{r.CustomerName} ({r.CustomerCode})"), ("Agreement", r.AgreementNumber), ("Booking", r.BookingNumber), ("Project", r.ProjectName),
            ("Payment method", r.MethodName),
        };
        if (!string.IsNullOrWhiteSpace(r.Reference)) rows.Add(("Reference", r.Reference!));
        if (!string.IsNullOrWhiteSpace(r.BankName)) rows.Add(("Bank", r.BankName!));
        if (r.InstrumentDate is { } d) rows.Add(("Instrument date", d.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)));
        rows.Add(("Status", r.Status.ToString()));
        foreach (var (k, v) in rows)
        {
            var lines = SimplePdfWriter.Wrap(v, 10, ContentWidth - 130, false);
            if (lines.Count == 0) lines.Add("");
            L.Ensure(lines.Count * 13 + 2);
            pdf.Text(Margin, L.Y - 10, k + ":", 10, true);
            foreach (var l in lines) { pdf.Text(Margin + 130, L.Y - 10, l, 10, false); L.Y -= 13; }
            L.Y -= 2;
        }

        L.Ensure(70);
        L.Y -= 6;
        pdf.Rect(Margin, L.Y - 36, ContentWidth, 36, fillGray: 0.93, strokeWidth: 0.5);
        pdf.Text(Margin + 10, L.Y - 24, "Amount received", 11, false);
        pdf.Text(PageWidth - Margin - 10 - SimplePdfWriter.Width(money, 16, true), L.Y - 25, money, 16, true);
        L.Y -= 46;
        foreach (var line in SimplePdfWriter.Wrap("Amount in words: " + r.AmountInWords, 10, ContentWidth, true)) { L.Ensure(14); pdf.Text(Margin, L.Y - 10, line, 10, true); L.Y -= 13; }
        L.Y -= 8;

        Section(L, "Allocation", r.Allocations, false);
        Section(L, "Verification", r.Verification, true);
        Section(L, "Reversal history", r.ReversalHistory, false);

        // ---- footer note ----
        L.Ensure(40);
        L.Y -= 8;
        pdf.Line(Margin, L.Y, PageWidth - Margin, L.Y, 0.5);
        L.Y -= 14;
        pdf.Text(Margin, L.Y - 8, $"Printed by {r.PrintedBy} at {r.PrintedAtUtc.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture)} UTC", 9, false);
        pdf.Text(Margin, L.Y - 21, "This is a computer-generated receipt", 9, false);

        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SetPage(i);
            var t = $"Page {i + 1} of {pdf.PageCount}";
            pdf.Text(PageWidth - Margin - SimplePdfWriter.Width(t, 8, false), 28, t, 8, false);
            pdf.Text(Margin, 28, r.ReceiptNumber + " - " + r.CopyLabel, 8, false);
        }
        return pdf.ToBytes();
    }

    private sealed class Cursor(SimplePdfWriter pdf)
    {
        public double Y;
        public SimplePdfWriter Pdf => pdf;

        /// <summary>Starts a new page when fewer than <paramref name="needed"/> points remain above the footer area.</summary>
        public void Ensure(double needed)
        {
            if (Y - needed >= Bottom) return;
            pdf.AddPage();
            Y = PageHeight - Margin;
        }
    }

    private static void Section(Cursor L, string title, IReadOnlyList<ReceiptLine> lines, bool always)
    {
        if (lines.Count == 0 && !always) return;
        var pdf = L.Pdf;
        L.Ensure(48);
        L.Y -= 6;
        pdf.Text(Margin, L.Y - 12, title, 12, true);
        L.Y -= 16;
        pdf.Line(Margin, L.Y, PageWidth - Margin, L.Y, 0.5);
        L.Y -= 4;
        foreach (var l in lines)
        {
            var labelLines = SimplePdfWriter.Wrap(l.Label, 9, ContentWidth * 0.6 - 8, false);
            var valueLines = SimplePdfWriter.Wrap(l.Value, 9, ContentWidth * 0.4 - 8, false);
            var n = Math.Max(Math.Max(labelLines.Count, valueLines.Count), 1);
            L.Ensure(n * 12 + 4);
            for (var i = 0; i < labelLines.Count; i++) pdf.Text(Margin, L.Y - 10 - i * 12, labelLines[i], 9, false);
            for (var i = 0; i < valueLines.Count; i++)
                pdf.Text(PageWidth - Margin - SimplePdfWriter.Width(valueLines[i], 9, false), L.Y - 10 - i * 12, valueLines[i], 9, false);
            L.Y -= n * 12 + 3;
        }
        L.Y -= 4;
    }
}

/// <summary>Minimal PDF 1.4 writer: A4 pages, Helvetica / Helvetica-Bold, text, lines and rectangles.</summary>
internal sealed class SimplePdfWriter(double width, double height)
{
    private readonly List<StringBuilder> pages = [];
    private int current = -1;

    public int PageCount => pages.Count;

    public void AddPage() { pages.Add(new StringBuilder()); current = pages.Count - 1; }

    public void SetPage(int index) => current = index;

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Standard-font text restricted to printable ASCII (anything else becomes '?'), with \ ( ) escaped.</summary>
    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                default: sb.Append(ch is >= ' ' and <= '~' ? ch : '?'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Approximate string width in points (Helvetica average glyph width).</summary>
    public static double Width(string s, double size, bool bold) => s.Length * size * (bold ? 0.58 : 0.54);

    public static List<string> Wrap(string text, double size, double maxWidth, bool bold)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var max = Math.Max(8, (int)(maxWidth / (size * (bold ? 0.58 : 0.54))));
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word0 in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = word0;
                while (word.Length > max)
                {
                    if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); }
                    result.Add(word[..max]);
                    word = word[max..];
                }
                if (line.Length + word.Length + (line.Length > 0 ? 1 : 0) > max) { result.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) result.Add(line.ToString());
        }
        return result;
    }

    public void Text(double x, double y, string text, double size, bool bold, bool whiteText = false) =>
        pages[current].Append(whiteText ? "1 g\n" : "0 g\n").Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(F(size)).Append(" Tf ").Append(F(x)).Append(' ').Append(F(y))
            .Append(" Td (").Append(Escape(text)).Append(") Tj ET\n").Append("0 g\n");

    public void Line(double x1, double y1, double x2, double y2, double lineWidth) =>
        pages[current].Append("0 G ").Append(F(lineWidth)).Append(" w ").Append(F(x1)).Append(' ').Append(F(y1)).Append(" m ").Append(F(x2)).Append(' ').Append(F(y2)).Append(" l S\n");

    public void Rect(double x, double y, double w, double h, double? fillGray, double strokeWidth, (double R, double G, double B)? fillRgb = null)
    {
        var sb = pages[current];
        if (fillRgb is { } c) sb.Append(F(c.R)).Append(' ').Append(F(c.G)).Append(' ').Append(F(c.B)).Append(" rg\n");
        else if (fillGray is { } g) sb.Append(F(g)).Append(" g\n");
        sb.Append("0 G ").Append(F(strokeWidth)).Append(" w ").Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re ");
        var fill = fillRgb is not null || fillGray is not null;
        sb.Append(fill && strokeWidth > 0 ? "B" : fill ? "f" : "S").Append('\n').Append("0 g\n");
    }

    public byte[] ToBytes()
    {
        var offsets = new List<long>();
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add(ms.Position); Write($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        Write("%PDF-1.4\n%????\n");
        // objects: 1 catalog, 2 pages, 3 F1, 4 F2, then (page, content) pairs
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{5 + i * 2} 0 R"));
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pages.Count; i++)
        {
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(width)} {F(height)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + i * 2} 0 R >>");
            var content = pages[i].ToString();
            Obj($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        var xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
