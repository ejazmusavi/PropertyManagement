using System.Text;
using System.Text.RegularExpressions;
using RealEstate.Application.Payments;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure.Documents;

namespace RealEstate.Infrastructure.Tests;

public class ReceiptPdfRendererTests
{
    private static ReceiptDocument Doc(string copy = "ORIGINAL", int copyNo = 1, int allocations = 2, PaymentStatus status = PaymentStatus.Posted, string reference = "CHQ (123) \\ 9") => new(
        "Acme Estates (Pvt) Ltd", "Main Boulevard, Lahore", "042-1234567", "RCT-2026-000001", copy, copyNo, "PAY-2026-000001", new DateOnly(2026, 11, 20), new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc),
        "Test Customer", "CUS-0001", "AGR-1", "BK-1", "Green Valley", "Cash", reference, "HBL", new DateOnly(2026, 11, 19), "Rs", 125_000.5m,
        "Pakistani Rupees One Hundred Twenty Five Thousand and Fifty Paisa Only", status,
        Enumerable.Range(1, allocations).Select(i => new ReceiptLine($"Line {i}: Monthly installment (due 01-Jan-2027) with a rather long descriptive text to wrap around the column", $"Rs {i * 1000:N2}")).ToList(),
        [new ReceiptLine("Verification", "Not required")], status == PaymentStatus.Reversed ? [new ReceiptLine("REVERSED on today", "Entered in error ₨")] : [],
        "Cashier One", new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc));

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Output_IsWellFormed_WithCorrectXref()
    {
        var bytes = new ReceiptPdfRenderer().Render(Doc());
        var text = Text(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        var startxref = long.Parse(Regex.Match(text, @"startxref\s+(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[(int)startxref..]);
        var offsets = Regex.Matches(text[(int)startxref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(offsets);
        for (var i = 0; i < offsets.Count; i++) Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..]);
        Assert.Contains($"/Size {offsets.Count + 1}", text);
    }

    [Fact]
    public void Output_ContainsReceiptNumberAndCopyLabel()
    {
        var text = Text(new ReceiptPdfRenderer().Render(Doc()));
        Assert.Contains("RCT-2026-000001", text);
        Assert.Contains("(ORIGINAL)", text);
        Assert.Contains("PAYMENT RECEIPT", text);
        Assert.Contains("This is a computer-generated receipt", text);
        Assert.Contains("Printed by Cashier One at 20-Nov-2026 09:00 UTC", text);
        var dup = Text(new ReceiptPdfRenderer().Render(Doc("DUPLICATE COPY 2", 3)));
        Assert.Contains("(DUPLICATE COPY 2)", dup);
        Assert.DoesNotContain("(ORIGINAL)", dup.Replace("RCT-2026-000001 - ORIGINAL", ""));
    }

    [Fact]
    public void Text_IsEscaped_AndNonAsciiReplaced()
    {
        var text = Text(new ReceiptPdfRenderer().Render(Doc(status: PaymentStatus.Reversed)));
        Assert.Contains(@"Acme Estates \(Pvt\) Ltd", text);
        Assert.Contains(@"CHQ \(123\) \\ 9", text);
        Assert.Contains("Entered in error ?", text);
        Assert.Contains("REVERSED", text);
    }

    [Fact]
    public void LongAllocationLists_SpanMultiplePages()
    {
        var text = Text(new ReceiptPdfRenderer().Render(Doc(allocations: 120)));
        var pages = Regex.Matches(text, "/Type /Page /Parent").Count;
        Assert.True(pages >= 3, $"expected several pages, got {pages}");
        Assert.Contains($"/Count {pages}", text);
        Assert.Contains($"Page {pages} of {pages}", text);
        Assert.Contains("Line 120:", text);
    }
}
