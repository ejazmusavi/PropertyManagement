using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RealEstate.Application.Common;
using RealEstate.Application.Customers;
using RealEstate.Application.Inventory;
using RealEstate.Application.Payments;
using RealEstate.Application.Projects;
using RealEstate.Application.Recovery;
using RealEstate.Application.Reporting;
using RealEstate.Application.Sales;

namespace RealEstate.Application.Assistant;

/// <summary>Per-question state the tools write to (the navigation to perform) and read from (the pages the user may open).</summary>
public sealed class AssistantTurn(IReadOnlyList<AssistantPage> allowedPages)
{
    public IReadOnlyList<AssistantPage> AllowedPages { get; } = allowedPages;
    public AssistantNavigation? Navigation { get; set; }
}

/// <summary>Raised for bad model-supplied arguments; the message is returned to the model so it can correct the call.</summary>
public sealed class AssistantToolException(string message) : Exception(message);

/// <summary>
/// The assistant's read-only tool belt. Every data tool calls the same Application service the screens use, so permission, project-scope and customer-scope checks are the
/// ones that already protect the UI and API (a refused call comes back to the model as an error). Results are trimmed: no identity numbers, no free-text notes, capped rows.
/// </summary>
public sealed class AssistantTools(CustomerService customers, PropertyService properties, BookingService bookings, PaymentService payments, ProjectService projects,
    DashboardService dashboards, ReportingService reports, RecoveryService recovery)
{
    public const int MaxRows = 25;
    private const int MaxResultChars = 14000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string LimitProp = "\"limit\":{\"type\":\"integer\",\"description\":\"Max rows, 1-25 (default 10)\"}";

    private static string Obj(string props) => "{\"type\":\"object\",\"properties\":{" + props.Trim() + "," + LimitProp + "}}";

    public static readonly IReadOnlyList<LlmToolDefinition> Definitions =
    [
        new("open_page", "Open a page of the application in the user's browser. Use for requests like 'open customers', 'go to overdue installments', 'show booking 12'. Only the listed page keys exist.",
            """
            {"type":"object","properties":{"page":{"type":"string","description":"A page key from the allowed list"},"id":{"type":"integer","description":"Optional record id for a detail page (customer, booking, property, payment, project, ...)"}},"required":["page"]}
            """),
        new("search_customers", "Find customers by name, code, mobile or e-mail.",
            Obj("""
            "search":{"type":"string"},"status":{"type":"string","enum":["Active","Inactive","Blocked"]}
            """)),
        new("search_properties", "Find properties/plots/units. Use list_projects first when you need a project id.",
            Obj("""
            "search":{"type":"string"},"project_id":{"type":"integer"},"status":{"type":"string","enum":["Available","Reserved","Booked","Sold","Allocated","PossessionReady","PossessionDelivered","Transferred","Cancelled"]},"min_size":{"type":"number"},"max_size":{"type":"number"}
            """)),
        new("search_bookings", "Find bookings (sales).",
            Obj("""
            "search":{"type":"string"},"project_id":{"type":"integer"},"customer_id":{"type":"integer"},"status":{"type":"string","enum":["Draft","PendingApproval","Confirmed","Rejected","Cancelled"]},"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"}
            """)),
        new("search_payments", "Find customer payments received.",
            Obj("""
            "search":{"type":"string"},"project_id":{"type":"integer"},"customer_id":{"type":"integer"},"status":{"type":"string","enum":["PendingVerification","PendingApproval","Posted","Rejected","Reversed"]},"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"}
            """)),
        new("list_projects", "List projects (id, code, name, city, status).",
            Obj("""
            "search":{"type":"string"}
            """)),
        new("overdue_installments", "Overdue installment lines with outstanding amounts and an aging summary.",
            Obj("""
            "project_id":{"type":"integer"},"customer_id":{"type":"integer"},"min_days_overdue":{"type":"integer"}
            """)),
        new("upcoming_installments", "Installments falling due soon.",
            Obj("""
            "project_id":{"type":"integer"},"customer_id":{"type":"integer"},"days_ahead":{"type":"integer","description":"default 30"}
            """)),
        new("company_summary", "Company-wide KPIs: projects, inventory by status, bookings, collections, receivables, overdue, expenses, and per-project rows.",
            """
            {"type":"object","properties":{"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"}}}
            """),
        new("list_reports", "List the reports the user may run (key, title, group, description, filters). Call before run_report.",
            """
            {"type":"object","properties":{}}
            """),
        new("run_report", "Run one report by key and return its rows and totals (first 25 rows). Use list_reports for keys and the filters each report accepts.",
            """
            {"type":"object","properties":{"key":{"type":"string"},"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"},"project_id":{"type":"integer"},"customer_id":{"type":"integer"},"status":{"type":"string"},"transaction_type":{"type":"string"}},"required":["key"]}
            """),
    ];

    public async Task<string> ExecuteAsync(string name, string argumentsJson, AssistantTurn turn, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var a = new Args(doc.RootElement);
        object result = name switch
        {
            "open_page" => OpenPage(a, turn),
            "search_customers" => await SearchCustomersAsync(a, ct),
            "search_properties" => await SearchPropertiesAsync(a, ct),
            "search_bookings" => await SearchBookingsAsync(a, ct),
            "search_payments" => await SearchPaymentsAsync(a, ct),
            "list_projects" => await ListProjectsAsync(a, ct),
            "overdue_installments" => await DuesAsync(a, true, ct),
            "upcoming_installments" => await DuesAsync(a, false, ct),
            "company_summary" => await CompanySummaryAsync(a, ct),
            "list_reports" => await ListReportsAsync(ct),
            "run_report" => await RunReportAsync(a, ct),
            _ => throw new AssistantToolException($"Unknown tool '{name}'."),
        };
        var text = JsonSerializer.Serialize(result, Json);
        return text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "\n…(truncated; narrow the filters)";
    }

    // ---------------------------------------------------------------------------------------------- navigation
    private static object OpenPage(Args a, AssistantTurn turn)
    {
        var key = a.Str("page") ?? throw new AssistantToolException("page is required.");
        var page = AssistantPageCatalog.Find(key) ?? throw new AssistantToolException($"Unknown page '{key}'. Allowed: {string.Join(", ", turn.AllowedPages.Select(p => p.Key))}.");
        if (turn.AllowedPages.All(p => p.Key != page.Key)) throw new AssistantToolException($"The user is not allowed to open '{page.Title}'.");
        var id = a.Long("id");
        if (id is not null && page.DetailPath is null) throw new AssistantToolException($"'{page.Title}' has no detail page; omit id.");
        turn.Navigation = new AssistantNavigation(AssistantPageCatalog.Resolve(page, id), id is null ? page.Title : $"{page.Title} #{id}");
        return new { opened = turn.Navigation.Title };
    }

    // ---------------------------------------------------------------------------------------------- data
    private async Task<object> SearchCustomersAsync(Args a, CancellationToken ct)
    {
        var r = Page(new CustomerListRequest { Status = a.Enum<RealEstate.Domain.Enums.CustomerStatus>("status") }, a);
        var p = await customers.ListAsync(r, ct);
        return Shape(p, c => new { c.Id, c.CustomerCode, name = c.FullName, c.Mobile, c.Email, c.Status, c.ActiveOwnerships });
    }

    private async Task<object> SearchPropertiesAsync(Args a, CancellationToken ct)
    {
        var r = Page(new PropertyListRequest { ProjectId = a.Long("project_id"), Status = a.Enum<RealEstate.Domain.Enums.PropertyStatus>("status"), MinSize = a.Dec("min_size"), MaxSize = a.Dec("max_size") }, a);
        var p = await properties.ListAsync(r, ct);
        return Shape(p, x => new { x.Id, x.ProjectCode, x.PropertyCode, x.PropertyNumber, x.Block, x.Sector, x.Street, type = x.TypeName, x.Size, x.Unit, x.Status, x.IsCorner, x.FacesMainRoad });
    }

    private async Task<object> SearchBookingsAsync(Args a, CancellationToken ct)
    {
        var r = Page(new BookingListRequest { ProjectId = a.Long("project_id"), CustomerId = a.Long("customer_id"), Status = a.Enum<RealEstate.Domain.Enums.BookingStatus>("status"), From = a.Date("from"), To = a.Date("to") }, a);
        var p = await bookings.ListAsync(r, ct);
        return Shape(p, b => new { b.Id, b.BookingNumber, b.ProjectCode, date = b.BookingDate, b.Status, b.PaymentMode, customer = b.PrimaryCustomer, b.Properties, b.TotalPayable });
    }

    private async Task<object> SearchPaymentsAsync(Args a, CancellationToken ct)
    {
        var r = Page(new PaymentListRequest { ProjectId = a.Long("project_id"), CustomerId = a.Long("customer_id"), Status = a.Enum<RealEstate.Domain.Enums.PaymentStatus>("status"), From = a.Date("from"), To = a.Date("to") }, a);
        var p = await payments.ListAsync(r, ct);
        return Shape(p, x => new { x.Id, x.PaymentNumber, x.ProjectCode, x.AgreementNumber, customer = x.CustomerName, method = x.MethodName, x.Amount, date = x.PaymentDate, x.Status, x.ReceiptNumber });
    }

    private async Task<object> ListProjectsAsync(Args a, CancellationToken ct)
    {
        var p = await projects.ListAsync(Page(new ProjectListRequest(), a), ct);
        return Shape(p, x => new { x.Id, x.ProjectCode, name = x.ProjectName, x.City, x.Status, area = x.CurrentArea, unit = x.MeasurementUnit });
    }

    private async Task<object> DuesAsync(Args a, bool overdue, CancellationToken ct)
    {
        var r = Page(new DueListRequest { ProjectId = a.Long("project_id"), CustomerId = a.Long("customer_id"), Days = overdue ? a.Int("min_days_overdue") : a.Int("days_ahead") }, a);
        var d = overdue ? await recovery.OverdueAsync(r, ct) : await recovery.UpcomingAsync(r, ct);
        return new
        {
            totalOutstanding = d.TotalOutstanding,
            aging = d.Aging,
            total = d.Lines.TotalCount,
            lines = d.Lines.Items.Select(l => new { l.AgreementNumber, l.ProjectCode, customer = l.CustomerName, l.CustomerId, l.LineNo, l.Description, due = l.DueDate, l.Outstanding, l.DaysOverdue, l.LateFee }),
        };
    }

    private async Task<object> CompanySummaryAsync(Args a, CancellationToken ct)
    {
        var d = await dashboards.CompanyAsync(new DashboardFilters(a.Date("from"), a.Date("to")), ct);
        return new
        {
            d.From, d.To, currency = d.CurrencySymbol, d.TotalProjects, d.ActiveProjects, d.TotalInventory, d.AvailableProperties, d.ReservedProperties, d.BookedProperties, d.SoldProperties,
            d.BookingsCount, d.TotalBookingValue, d.TotalCollections, d.CustomerReceivables, d.OverdueAmount, d.ApprovedExpenses, d.ExpensesPaid, d.CashBalance, d.BankBalance, d.Revenue, d.ProfitLoss,
            projects = d.Projects.Take(MaxRows).Select(p => new { p.ProjectId, p.ProjectCode, p.ProjectName, p.Inventory, p.Sold, p.Available, p.BookingValue, p.Collections, p.Outstanding, p.Overdue, p.Expenses }),
        };
    }

    private async Task<object> ListReportsAsync(CancellationToken ct) =>
        (await reports.ListAsync(ct)).Select(r => new { r.Key, r.Title, r.Group, r.Description, filters = r.Filters, r.RequiresCustomer, r.RequiresProject }).ToList();

    private async Task<object> RunReportAsync(Args a, CancellationToken ct)
    {
        var key = a.Str("key") ?? throw new AssistantToolException("key is required.");
        var t = await reports.RunAsync(key, new ReportFilters
        {
            From = a.Date("from"), To = a.Date("to"), ProjectId = a.Long("project_id"), CustomerId = a.Long("customer_id"), Status = a.Str("status"), TransactionType = a.Str("transaction_type"),
            Page = 1, PageSize = MaxRows,
        }, ct);
        return new { t.Title, columns = t.Columns.Select(c => c.Header), rows = t.Rows, totals = t.Totals, totalRows = t.TotalRows, currency = t.CurrencySymbol, notes = t.Notes };
    }

    // ---------------------------------------------------------------------------------------------- helpers
    private static T Page<T>(T request, Args a) where T : PagedRequest
    {
        request.Page = 1;
        request.PageSize = Math.Clamp(a.Int("limit") ?? 10, 1, MaxRows);
        request.Search = a.Str("search");
        return request;
    }

    private static object Shape<TIn, TOut>(PagedResult<TIn> p, Func<TIn, TOut> map) => new { total = p.TotalCount, shown = p.Items.Count, items = p.Items.Select(map) };

    /// <summary>Lenient, validating reader for model-supplied arguments.</summary>
    private sealed class Args(JsonElement root)
    {
        private JsonElement? Get(string n) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(n, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

        public string? Str(string n) => Get(n) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : v.ToString()) is { Length: > 0 } s ? s[..Math.Min(s.Length, 200)] : null : null;

        public long? Long(string n) => Get(n) is not { } v ? null : v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
            : long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : throw new AssistantToolException($"{n} must be an integer.");

        public int? Int(string n) => Long(n) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

        public decimal? Dec(string n) => Get(n) is not { } v ? null : decimal.TryParse(v.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : throw new AssistantToolException($"{n} must be a number.");

        public DateOnly? Date(string n) => Str(n) is not { } s ? null : DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new AssistantToolException($"{n} must be yyyy-MM-dd.");

        public TEnum? Enum<TEnum>(string n) where TEnum : struct, Enum => Str(n) is not { } s ? null
            : System.Enum.TryParse<TEnum>(s, true, out var e) && System.Enum.IsDefined(e) ? e : throw new AssistantToolException($"{n} must be one of: {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
    }
}
