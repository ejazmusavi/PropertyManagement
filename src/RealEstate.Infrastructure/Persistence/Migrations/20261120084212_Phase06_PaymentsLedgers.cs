using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase06_PaymentsLedgers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentRevision",
                table: "CustomerAgreements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AccountingPostingRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EventType = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceType = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PayloadJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPostingRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingPostingRequests_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentMethods",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kind = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequiresVerification = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RequiresReference = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsSystem = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethods", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RecoveryAssignments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    OfficerUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OfficerName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AssignedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    AssignedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActiveAgreementId = table.Column<long>(type: "bigint", nullable: true),
                    OverdueAtAssignment = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClosedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CloseReason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryAssignments_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecoveryAssignments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecoveryAssignments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentNumber = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    BookingId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    PaymentMethodId = table.Column<long>(type: "bigint", nullable: false),
                    MethodKind = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Source = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BankName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InstrumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PayerName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Remarks = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActiveReferenceKey = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedAllocationMode = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedAllocationJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedChargeId = table.Column<long>(type: "bigint", nullable: true),
                    RequestedRemainderToAdvance = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerificationRequired = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    SubmittedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ApprovedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RejectedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RejectReason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReversedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_Amount", "`Amount` > 0");
                    table.ForeignKey(
                        name: "FK_Payments_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RefundNumber = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    BookingRefundId = table.Column<long>(type: "bigint", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedByUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RequestedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    PaymentMethodId = table.Column<long>(type: "bigint", nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentReference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaidBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CancelledBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CancelReason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refunds_Amount", "`Amount` > 0");
                    table.ForeignKey(
                        name: "FK_Refunds_BookingRefunds_BookingRefundId",
                        column: x => x.BookingRefundId,
                        principalTable: "BookingRefunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CustomerFollowUps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    AssignmentId = table.Column<long>(type: "bigint", nullable: true),
                    OfficerUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OfficerName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FollowUpDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Outcome = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PromisedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    PromisedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextFollowUpDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OverdueAtFollowUp = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerFollowUps", x => x.Id);
                    table.CheckConstraint("CK_CustomerFollowUps_Promise", "`PromisedAmount` IS NULL OR `PromisedAmount` > 0");
                    table.ForeignKey(
                        name: "FK_CustomerFollowUps_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerFollowUps_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerFollowUps_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerFollowUps_RecoveryAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "RecoveryAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentId = table.Column<long>(type: "bigint", nullable: false),
                    ReceiptNumber = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    IssuedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CopiesPrinted = table.Column<int>(type: "int", nullable: false),
                    LastPrintedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastPrintedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentReversals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReversedByUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReversedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReversedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    AllocationsReversed = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReversals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentReversals_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentVerifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentId = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerifiedByUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    VerifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Notes = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BankReference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClearedOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentVerifications_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AgreementLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    PostedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntryType = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Debit = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Credit = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Description = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaymentId = table.Column<long>(type: "bigint", nullable: true),
                    RefundId = table.Column<long>(type: "bigint", nullable: true),
                    ScheduleItemRootId = table.Column<long>(type: "bigint", nullable: true),
                    ScheduleItemId = table.Column<long>(type: "bigint", nullable: true),
                    UniqueKey = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgreementLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_AgreementLedgerEntries_Side", "`Debit` >= 0 AND `Credit` >= 0 AND ((`Debit` > 0 AND `Credit` = 0) OR (`Credit` > 0 AND `Debit` = 0))");
                    table.ForeignKey(
                        name: "FK_AgreementLedgerEntries_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementLedgerEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementLedgerEntries_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementLedgerEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementLedgerEntries_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RefundApprovals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RefundId = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ApproverUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ApproverName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Comments = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefundApprovals_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Mode = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ScheduleItemId = table.Column<long>(type: "bigint", nullable: true),
                    AgreementChargeId = table.Column<long>(type: "bigint", nullable: true),
                    RefundId = table.Column<long>(type: "bigint", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AllocatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    AllocatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CloseReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReversalId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.Id);
                    table.CheckConstraint("CK_PaymentAllocations_Amount", "`Amount` > 0");
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_AgreementCharges_AgreementChargeId",
                        column: x => x.AgreementChargeId,
                        principalTable: "AgreementCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentReversals_ReversalId",
                        column: x => x.ReversalId,
                        principalTable: "PaymentReversals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentScheduleItems_ScheduleItemId",
                        column: x => x.ScheduleItemId,
                        principalTable: "PaymentScheduleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CustomerLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    PostedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntryType = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Debit = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Credit = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Description = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaymentId = table.Column<long>(type: "bigint", nullable: true),
                    RefundId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_CustomerLedgerEntries_Side", "`Debit` >= 0 AND `Credit` >= 0 AND ((`Debit` > 0 AND `Credit` = 0) OR (`Credit` > 0 AND `Debit` = 0))");
                    table.ForeignKey(
                        name: "FK_CustomerLedgerEntries_AgreementLedgerEntries_AgreementLedger~",
                        column: x => x.AgreementLedgerEntryId,
                        principalTable: "AgreementLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerLedgerEntries_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerLedgerEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerLedgerEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostingRequests_EventType_SourceType_SourceId",
                table: "AccountingPostingRequests",
                columns: new[] { "EventType", "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostingRequests_ProjectId",
                table: "AccountingPostingRequests",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPostingRequests_Status",
                table: "AccountingPostingRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_AgreementId_EntryDate",
                table: "AgreementLedgerEntries",
                columns: new[] { "AgreementId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_AgreementId_ScheduleItemRootId",
                table: "AgreementLedgerEntries",
                columns: new[] { "AgreementId", "ScheduleItemRootId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_CustomerId",
                table: "AgreementLedgerEntries",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_PaymentId",
                table: "AgreementLedgerEntries",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_ProjectId",
                table: "AgreementLedgerEntries",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_RefundId",
                table: "AgreementLedgerEntries",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementLedgerEntries_UniqueKey",
                table: "AgreementLedgerEntries",
                column: "UniqueKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFollowUps_AgreementId_FollowUpDate",
                table: "CustomerFollowUps",
                columns: new[] { "AgreementId", "FollowUpDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFollowUps_AssignmentId",
                table: "CustomerFollowUps",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFollowUps_CustomerId",
                table: "CustomerFollowUps",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFollowUps_OfficerUserId_FollowUpDate",
                table: "CustomerFollowUps",
                columns: new[] { "OfficerUserId", "FollowUpDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerFollowUps_ProjectId",
                table: "CustomerFollowUps",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_AgreementId",
                table: "CustomerLedgerEntries",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_AgreementLedgerEntryId",
                table: "CustomerLedgerEntries",
                column: "AgreementLedgerEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_CustomerId_AgreementId",
                table: "CustomerLedgerEntries",
                columns: new[] { "CustomerId", "AgreementId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_CustomerId_EntryDate",
                table: "CustomerLedgerEntries",
                columns: new[] { "CustomerId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLedgerEntries_ProjectId",
                table: "CustomerLedgerEntries",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_AgreementChargeId",
                table: "PaymentAllocations",
                column: "AgreementChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_AgreementId_Status",
                table: "PaymentAllocations",
                columns: new[] { "AgreementId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_PaymentId_Status",
                table: "PaymentAllocations",
                columns: new[] { "PaymentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_RefundId",
                table: "PaymentAllocations",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_ReversalId",
                table: "PaymentAllocations",
                column: "ReversalId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_ScheduleItemId",
                table: "PaymentAllocations",
                column: "ScheduleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethods_Code",
                table: "PaymentMethods",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_PaymentId",
                table: "PaymentReceipts",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_ReceiptNumber",
                table: "PaymentReceipts",
                column: "ReceiptNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReversals_PaymentId",
                table: "PaymentReversals",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ActiveReferenceKey",
                table: "Payments",
                column: "ActiveReferenceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AgreementId",
                table: "Payments",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BookingId",
                table: "Payments",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CustomerId",
                table: "Payments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IdempotencyKey",
                table: "Payments",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentMethodId",
                table: "Payments",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentNumber",
                table: "Payments",
                column: "PaymentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProjectId",
                table: "Payments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Reference",
                table: "Payments",
                column: "Reference");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Status_PaymentDate",
                table: "Payments",
                columns: new[] { "Status", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVerifications_PaymentId",
                table: "PaymentVerifications",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAssignments_ActiveAgreementId",
                table: "RecoveryAssignments",
                column: "ActiveAgreementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAssignments_AgreementId",
                table: "RecoveryAssignments",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAssignments_CustomerId",
                table: "RecoveryAssignments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAssignments_OfficerUserId_Status",
                table: "RecoveryAssignments",
                columns: new[] { "OfficerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAssignments_ProjectId",
                table: "RecoveryAssignments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundApprovals_RefundId",
                table: "RefundApprovals",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_AgreementId",
                table: "Refunds",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_BookingRefundId",
                table: "Refunds",
                column: "BookingRefundId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_CustomerId",
                table: "Refunds",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_IdempotencyKey",
                table: "Refunds",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentMethodId",
                table: "Refunds",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_ProjectId",
                table: "Refunds",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_RefundNumber",
                table: "Refunds",
                column: "RefundNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status",
                table: "Refunds",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingPostingRequests");

            migrationBuilder.DropTable(
                name: "CustomerFollowUps");

            migrationBuilder.DropTable(
                name: "CustomerLedgerEntries");

            migrationBuilder.DropTable(
                name: "PaymentAllocations");

            migrationBuilder.DropTable(
                name: "PaymentReceipts");

            migrationBuilder.DropTable(
                name: "PaymentVerifications");

            migrationBuilder.DropTable(
                name: "RefundApprovals");

            migrationBuilder.DropTable(
                name: "RecoveryAssignments");

            migrationBuilder.DropTable(
                name: "AgreementLedgerEntries");

            migrationBuilder.DropTable(
                name: "PaymentReversals");

            migrationBuilder.DropTable(
                name: "Refunds");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "PaymentMethods");

            migrationBuilder.DropColumn(
                name: "PaymentRevision",
                table: "CustomerAgreements");
        }
    }
}
