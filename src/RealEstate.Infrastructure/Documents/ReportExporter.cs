using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using RealEstate.Application.Reporting;

namespace RealEstate.Infrastructure.Documents;

/// <summary>Exports report tables to PDF (landscape A4, repeated headers, page numbers) and Excel (.xlsx, typed cells) without third-party libraries.</summary>
public sealed class ReportExporter : IReportExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Format(object? v, ReportColumnKind kind) => v switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        decimal m when kind == ReportColumnKind.Percent => m.ToString("N2", Inv) + "%",
        decimal m => m.ToString("N2", Inv),
        int i => i.ToString("N0", Inv),
        long l => l.ToString("N0", Inv),
        _ => v.ToString() ?? "",
    };

    private static bool IsNumeric(ReportColumnKind k) => k is ReportColumnKind.Money or ReportColumnKind.Number or ReportColumnKind.Percent;

    // =====================================================================================================
    // PDF
    // =====================================================================================================
    private const double PageW = 842, PageH = 595, Margin = 30, FontSize = 8, RowH = 12;

    public byte[] ToPdf(ReportTableDto r)
    {
        var pdf = new SimplePdfWriter(PageW, PageH);
        var cols = r.Columns;
        var usable = PageW - 2 * Margin;
        // column weights from the longest formatted value (header included), clamped
        var weights = cols.Select((c, i) =>
        {
            var max = Math.Max(c.Header.Length, r.Rows.Take(200).Select(row => Format(row[i], c.Kind).Length).DefaultIfEmpty(0).Max());
            if (r.Totals is not null) max = Math.Max(max, Format(r.Totals[i], c.Kind).Length);
            return Math.Clamp(max, 6, 40);
        }).ToArray();
        var total = weights.Sum();
        var widths = weights.Select(w => usable * w / total).ToArray();

        var pageNo = 0;
        double y = 0;
        void NewPage()
        {
            pdf.AddPage(); pageNo++;
            y = PageH - Margin;
            pdf.Text(Margin, y - 12, r.Title, 14, true);
            y -= 18;
            var sub = $"{r.Group}  |  Generated {r.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC by {r.GeneratedBy}  |  Currency {r.CurrencySymbol}";
            pdf.Text(Margin, y - 8, sub, 8, false);
            y -= 12;
            if (r.Filters.Count > 0)
            {
                foreach (var line in SimplePdfWriter.Wrap(string.Join("   ", r.Filters.Select(f => $"{f.Label}: {f.Value}")), 8, usable, false)) { pdf.Text(Margin, y - 8, line, 8, false); y -= 11; }
            }
            y -= 4;
            DrawRow(cols.Select(c => c.Header).ToArray(), true, true);
        }
        void DrawRow(string[] cells, bool bold, bool header)
        {
            if (header) pdf.Rect(Margin, y - RowH + 2, usable, RowH, 0.9, 0);
            var x = Margin;
            for (var i = 0; i < cells.Length; i++)
            {
                var maxChars = Math.Max(3, (int)((widths[i] - 4) / (FontSize * (bold ? 0.58 : 0.54))));
                var text = cells[i].Length > maxChars ? cells[i][..Math.Max(1, maxChars - 1)] + "." : cells[i];
                var tw = SimplePdfWriter.Width(text, FontSize, bold);
                var tx = IsNumeric(cols[i].Kind) ? x + widths[i] - 2 - tw : x + 2;
                pdf.Text(tx, y - 8, text, FontSize, bold);
                x += widths[i];
            }
            y -= RowH;
            pdf.Line(Margin, y + 2, Margin + usable, y + 2, header ? 0.8 : 0.2);
        }

        NewPage();
        if (r.Rows.Count == 0) { pdf.Text(Margin, y - 10, "No data for the selected filters.", 10, false); y -= 16; }
        foreach (var row in r.Rows)
        {
            if (y < Margin + 40) NewPage();
            DrawRow(cols.Select((c, i) => Format(row[i], c.Kind)).ToArray(), false, false);
        }
        if (r.Totals is not null)
        {
            if (y < Margin + 40) NewPage();
            DrawRow(cols.Select((c, i) => Format(r.Totals[i], c.Kind)).ToArray(), true, false);
        }
        y -= 6;
        foreach (var note in r.Notes)
            foreach (var line in SimplePdfWriter.Wrap(note, 8, usable, false)) { if (y < Margin + 24) NewPage(); pdf.Text(Margin, y - 8, line, 8, false); y -= 11; }
        if (r.TotalRows > r.Rows.Count) { if (y < Margin + 24) NewPage(); pdf.Text(Margin, y - 8, $"Showing {r.Rows.Count:N0} of {r.TotalRows:N0} rows.", 8, true); }

        var pages = pdf.PageCount;
        for (var i = 0; i < pages; i++)
        {
            pdf.SetPage(i);
            var label = $"Page {i + 1} of {pages}";
            pdf.Text(PageW - Margin - SimplePdfWriter.Width(label, 8, false), 16, label, 8, false);
        }
        return pdf.ToBytes();
    }

    // =====================================================================================================
    // XLSX
    // =====================================================================================================
    // cell styles (cellXfs index): 0 default, 1 bold, 2 money, 3 date, 4 percent, 5 bold money, 6 bold percent, 7 integer, 8 title, 9 bold integer
    private static int Style(ReportColumnKind kind, bool bold) => kind switch
    {
        ReportColumnKind.Money => bold ? 5 : 2,
        ReportColumnKind.Date => 3,
        ReportColumnKind.Percent => bold ? 6 : 4,
        ReportColumnKind.Number => bold ? 9 : 7,
        _ => bold ? 1 : 0,
    };

    private static string ColName(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string Esc(string s) => SecurityElement.Escape(new string(s.Where(ch => ch >= ' ' || ch is '\t' or '\n' or '\r').ToArray())) ?? "";

    public byte[] ToExcel(ReportTableDto r)
    {
        var sheet = new StringBuilder();
        var row = 0;
        void Text(int col, string text, int style) => sheet.Append($"<c r=\"{ColName(col)}{row}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
        void Cell(int col, object? v, ReportColumnKind kind, bool bold)
        {
            switch (v)
            {
                case null: return;
                case decimal m: sheet.Append($"<c r=\"{ColName(col)}{row}\" s=\"{Style(kind, bold)}\"><v>{m.ToString(Inv)}</v></c>"); break;
                case int i: sheet.Append($"<c r=\"{ColName(col)}{row}\" s=\"{Style(ReportColumnKind.Number, bold)}\"><v>{i.ToString(Inv)}</v></c>"); break;
                case long l: sheet.Append($"<c r=\"{ColName(col)}{row}\" s=\"{Style(ReportColumnKind.Number, bold)}\"><v>{l.ToString(Inv)}</v></c>"); break;
                case DateOnly d: sheet.Append($"<c r=\"{ColName(col)}{row}\" s=\"3\"><v>{(d.ToDateTime(TimeOnly.MinValue) - new DateTime(1899, 12, 30)).Days}</v></c>"); break;
                default: Text(col, v.ToString() ?? "", bold ? 1 : 0); break;
            }
        }
        void Open() { row++; sheet.Append($"<row r=\"{row}\">"); }
        void Close() => sheet.Append("</row>");

        Open(); Text(0, r.Title, 8); Close();
        Open(); Text(0, $"{r.Group} | Generated {r.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC by {r.GeneratedBy} | Currency {r.CurrencySymbol}", 0); Close();
        foreach (var f in r.Filters) { Open(); Text(0, f.Label, 1); Text(1, f.Value, 0); Close(); }
        Open(); Close();
        Open();
        for (var i = 0; i < r.Columns.Count; i++) Text(i, r.Columns[i].Header, 1);
        Close();
        var headerRow = row;
        foreach (var data in r.Rows)
        {
            Open();
            for (var i = 0; i < r.Columns.Count; i++) Cell(i, data[i], r.Columns[i].Kind, false);
            Close();
        }
        if (r.Totals is not null)
        {
            Open();
            for (var i = 0; i < r.Columns.Count; i++) Cell(i, r.Totals[i], r.Columns[i].Kind, true);
            Close();
        }
        if (r.Notes.Count > 0) { Open(); Close(); }
        foreach (var n in r.Notes) { Open(); Text(0, n, 0); Close(); }

        var cols = new StringBuilder("<cols>");
        for (var i = 0; i < r.Columns.Count; i++)
        {
            var max = Math.Max(r.Columns[i].Header.Length, r.Rows.Take(500).Select(d => Format(d[i], r.Columns[i].Kind).Length).DefaultIfEmpty(0).Max());
            cols.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Math.Clamp(max + 2, 10, 60).ToString(Inv)}\" customWidth=\"1\"/>");
        }
        cols.Append("</cols>");

        var worksheet = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + $"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{headerRow}\" topLeftCell=\"A{headerRow + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>"
            + cols + "<sheetData>" + sheet + "</sheetData></worksheet>";

        const string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"#,##0.00\"/><numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\"/></numFmts>"
            + "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"10\">"
            + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"164\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"164\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
            + "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"3\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>"
            + "</cellXfs></styleSheet>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            var sheetName = Esc(new string(r.Title.Where(c => !"[]:*?/\\".Contains(c)).ToArray()));
            if (sheetName.Length > 31) sheetName = sheetName[..31];
            Add("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + $"<sheets><sheet name=\"{sheetName}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add("xl/styles.xml", styles);
            Add("xl/worksheets/sheet1.xml", worksheet);
        }
        return ms.ToArray();
    }
}
