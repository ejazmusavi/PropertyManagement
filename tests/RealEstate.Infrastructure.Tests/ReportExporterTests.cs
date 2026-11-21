using System.IO.Compression;
using System.Text;
using RealEstate.Application.Reporting;
using RealEstate.Infrastructure.Documents;

namespace RealEstate.Infrastructure.Tests;

/// <summary>Report export documents (no database): valid PDF structure with paging, valid xlsx package with typed cells and escaping.</summary>
public class ReportExporterTests
{
    private static ReportTableDto Table(int rows, string title = "Daily collection")
    {
        var cols = new[]
        {
            new ReportColumnDto("date", "Date", ReportColumnKind.Date), new ReportColumnDto("name", "Customer", ReportColumnKind.Text), new ReportColumnDto("count", "Payments", ReportColumnKind.Number),
            new ReportColumnDto("amount", "Amount", ReportColumnKind.Money), new ReportColumnDto("share", "Share", ReportColumnKind.Percent),
        };
        var data = Enumerable.Range(0, rows).Select(i => (IReadOnlyList<object?>)new object?[] { new DateOnly(2026, 1, 1).AddDays(i), $"Customer <{i}> & \"Sons\"", i, 1234.5m + i, 12.5m }).ToList();
        return new ReportTableDto("daily-collection", title, "Collection reports", cols, data, ["Total", "", rows, 1234.5m * rows, null], rows, 1, Math.Max(rows, 1),
            [new ReportFilterEchoDto("Project", "P1 Serene")], ["A note."], new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc), "tester", "Rs", false);
    }

    [Fact]
    public void Pdf_IsAWellFormedMultiPageDocument_WithRepeatedHeadersAndPageNumbers()
    {
        var bytes = new ReportExporter().ToPdf(Table(120));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        var pages = System.Text.RegularExpressions.Regex.Matches(text, "/Type /Page /Parent").Count;
        Assert.True(pages >= 3, $"pages: {pages}");
        Assert.Contains($"Page 1 of {pages}", text);
        Assert.Contains($"Page {pages} of {pages}", text);
        Assert.Equal(pages, System.Text.RegularExpressions.Regex.Matches(text, @"\(Customer\) Tj").Count);   // the header row is repeated on every page
        Assert.Contains("Daily collection", text);
        Assert.Contains("Project: P1 Serene", text);
        Assert.Contains("1,234.50", text);
        // xref offsets point at the objects
        var xref = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[xref..]);
    }

    [Fact]
    public void Pdf_OfAnEmptyReport_SaysThereIsNoData()
    {
        var empty = Table(0) with { Rows = [], Totals = null, TotalRows = 0 };
        var text = Encoding.ASCII.GetString(new ReportExporter().ToPdf(empty));
        Assert.Contains("No data for the selected filters.", text);
    }

    [Fact]
    public void Excel_IsAValidPackage_WithTypedCells_Totals_Notes_AndEscaping()
    {
        var bytes = new ReportExporter().ToExcel(Table(3, "Sales: Q1/Q2 [draft]"));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" }) Assert.NotNull(zip.GetEntry(name));
        string Read(string n) { using var r = new StreamReader(zip.GetEntry(n)!.Open()); return r.ReadToEnd(); }
        var sheet = Read("xl/worksheets/sheet1.xml");
        var workbook = Read("xl/workbook.xml");
        Assert.DoesNotContain("[", workbook.Split("name=\"")[1].Split('"')[0]);                     // sheet names cannot contain [ ] : / etc.
        Assert.Contains("Customer &lt;0&gt; &amp; &quot;Sons&quot;", sheet);                       // escaped text
        Assert.Contains("<v>1234.5</v>", sheet);                                                   // numeric, not text
        Assert.Contains("<v>46023</v>", sheet);                                                     // 2026-01-01 as an Excel date serial
        Assert.Contains("A note.", sheet);
        Assert.Contains("<pane ySplit=", sheet);                                                    // frozen header
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(sheet, @"<c r=""D\d+"" s=""[25]""><v>").Count);   // 3 data rows + the total row use the money styles
        // well-formed XML
        System.Xml.Linq.XDocument.Parse(sheet); System.Xml.Linq.XDocument.Parse(Read("xl/styles.xml")); System.Xml.Linq.XDocument.Parse(workbook);
    }
}
