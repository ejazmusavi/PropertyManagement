using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase05_InstallmentEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InstallmentPlanId",
                table: "CustomerAgreements",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduleStartDate",
                table: "CustomerAgreements",
                type: "date",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ToVersionNo",
                table: "AgreementAmendments",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<Guid>(
                name: "ApprovedByUserId",
                table: "AgreementAmendments",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AlterColumn<string>(
                name: "ApprovedBy",
                table: "AgreementAmendments",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "BaseScheduleId",
                table: "AgreementAmendments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecidedAtUtc",
                table: "AgreementAmendments",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionComments",
                table: "AgreementAmendments",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "AgreementAmendments",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "PendingAgreementId",
                table: "AgreementAmendments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "AgreementAmendments",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "RequestedByUserId",
                table: "AgreementAmendments",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<long>(
                name: "ResultScheduleId",
                table: "AgreementAmendments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RowVersion",
                table: "AgreementAmendments",
                type: "timestamp(6)",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "AgreementAmendments",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Approved")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ChargeTypes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kind = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DefaultCategory = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DefaultFrequency = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_ChargeTypes", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InstallmentPlans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PlanType = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PropertyCategoryId = table.Column<long>(type: "bigint", nullable: true),
                    PropertyId = table.Column<long>(type: "bigint", nullable: true),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActivatedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ActivatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RetiredBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RetireReason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
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
                    table.PrimaryKey("PK_InstallmentPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstallmentPlans_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstallmentPlans_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstallmentPlans_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstallmentPlans_PropertyCategories_PropertyCategoryId",
                        column: x => x.PropertyCategoryId,
                        principalTable: "PropertyCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "LateFeeRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    Method = table.Column<string>(type: "varchar(25)", maxLength: 25, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    MaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MaxPercentOfInstallment = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
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
                    table.PrimaryKey("PK_LateFeeRules", x => x.Id);
                    table.CheckConstraint("CK_LateFeeRules_MaxAmount", "`MaxAmount` IS NULL OR `MaxAmount` > 0");
                    table.CheckConstraint("CK_LateFeeRules_MaxPercent", "`MaxPercentOfInstallment` IS NULL OR (`MaxPercentOfInstallment` > 0 AND `MaxPercentOfInstallment` <= 100)");
                    table.CheckConstraint("CK_LateFeeRules_Value", "`Value` > 0");
                    table.ForeignKey(
                        name: "FK_LateFeeRules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentSchedules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActiveAgreementId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceAmendmentId = table.Column<long>(type: "bigint", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalTax = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    GeneratedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SupersededAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSchedules", x => x.Id);
                    table.CheckConstraint("CK_PaymentSchedules_Total", "`TotalAmount` > 0");
                    table.ForeignKey(
                        name: "FK_PaymentSchedules_AgreementAmendments_SourceAmendmentId",
                        column: x => x.SourceAmendmentId,
                        principalTable: "AgreementAmendments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentSchedules_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "InstallmentPlanComponents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PlanId = table.Column<long>(type: "bigint", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ChargeTypeId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Category = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AmountBasis = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Percent = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Frequency = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NumberOfInstallments = table.Column<int>(type: "int", nullable: true),
                    InstallmentAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FirstDueOffsetMonths = table.Column<int>(type: "int", nullable: false),
                    FirstDueOffsetDays = table.Column<int>(type: "int", nullable: false),
                    DueDayOfMonth = table.Column<int>(type: "int", nullable: true),
                    CustomScheduleJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GracePeriodDays = table.Column<int>(type: "int", nullable: false),
                    LateFeeRuleId = table.Column<long>(type: "bigint", nullable: true),
                    WaiverAllowed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MaxWaiverPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TaxTreatment = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RoundingUnit = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Remarks = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
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
                    table.PrimaryKey("PK_InstallmentPlanComponents", x => x.Id);
                    table.CheckConstraint("CK_InstallmentPlanComponents_Amount", "`Amount` IS NULL OR `Amount` > 0");
                    table.CheckConstraint("CK_InstallmentPlanComponents_Percent", "`Percent` IS NULL OR (`Percent` > 0 AND `Percent` <= 100)");
                    table.ForeignKey(
                        name: "FK_InstallmentPlanComponents_ChargeTypes_ChargeTypeId",
                        column: x => x.ChargeTypeId,
                        principalTable: "ChargeTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstallmentPlanComponents_InstallmentPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "InstallmentPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InstallmentPlanComponents_LateFeeRules_LateFeeRuleId",
                        column: x => x.LateFeeRuleId,
                        principalTable: "LateFeeRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AgreementCharges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ChargeTypeId = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Category = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Frequency = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NumberOfInstallments = table.Column<int>(type: "int", nullable: true),
                    InstallmentAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FirstDueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDayOfMonth = table.Column<int>(type: "int", nullable: true),
                    CustomScheduleJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GracePeriodDays = table.Column<int>(type: "int", nullable: false),
                    LateFeeRuleId = table.Column<long>(type: "bigint", nullable: true),
                    WaiverAllowed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MaxWaiverPercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TaxTreatment = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RoundingUnit = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Remarks = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourcePlanComponentId = table.Column<long>(type: "bigint", nullable: true),
                    AddedByAmendmentId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgreementCharges", x => x.Id);
                    table.CheckConstraint("CK_AgreementCharges_Amount", "`Amount` > 0");
                    table.ForeignKey(
                        name: "FK_AgreementCharges_AgreementAmendments_AddedByAmendmentId",
                        column: x => x.AddedByAmendmentId,
                        principalTable: "AgreementAmendments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementCharges_ChargeTypes_ChargeTypeId",
                        column: x => x.ChargeTypeId,
                        principalTable: "ChargeTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementCharges_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementCharges_InstallmentPlanComponents_SourcePlanCompone~",
                        column: x => x.SourcePlanComponentId,
                        principalTable: "InstallmentPlanComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgreementCharges_LateFeeRules_LateFeeRuleId",
                        column: x => x.LateFeeRuleId,
                        principalTable: "LateFeeRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentScheduleItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ScheduleId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementChargeId = table.Column<long>(type: "bigint", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    InstallmentNo = table.Column<int>(type: "int", nullable: false),
                    InstallmentCount = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    GraceEndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsPartial = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AmountWaived = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LateFeeWaived = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LastPaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreviousItemId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentScheduleItems", x => x.Id);
                    table.CheckConstraint("CK_PaymentScheduleItems_Amount", "`Amount` > 0");
                    table.CheckConstraint("CK_PaymentScheduleItems_Settlement", "`AmountPaid` >= 0 AND `AmountWaived` >= 0 AND `LateFeeWaived` >= 0 AND `AmountPaid` + `AmountWaived` <= `Amount`");
                    table.ForeignKey(
                        name: "FK_PaymentScheduleItems_AgreementCharges_AgreementChargeId",
                        column: x => x.AgreementChargeId,
                        principalTable: "AgreementCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentScheduleItems_PaymentScheduleItems_PreviousItemId",
                        column: x => x.PreviousItemId,
                        principalTable: "PaymentScheduleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentScheduleItems_PaymentSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "PaymentSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ScheduleAmendments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AmendmentId = table.Column<long>(type: "bigint", nullable: false),
                    FromScheduleId = table.Column<long>(type: "bigint", nullable: false),
                    ToScheduleId = table.Column<long>(type: "bigint", nullable: false),
                    Operation = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgreementChargeId = table.Column<long>(type: "bigint", nullable: true),
                    FromItemId = table.Column<long>(type: "bigint", nullable: true),
                    ToItemId = table.Column<long>(type: "bigint", nullable: true),
                    OldDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NewDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OldAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    NewAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleAmendments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleAmendments_AgreementAmendments_AmendmentId",
                        column: x => x.AmendmentId,
                        principalTable: "AgreementAmendments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleAmendments_AgreementCharges_AgreementChargeId",
                        column: x => x.AgreementChargeId,
                        principalTable: "AgreementCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleAmendments_PaymentScheduleItems_FromItemId",
                        column: x => x.FromItemId,
                        principalTable: "PaymentScheduleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleAmendments_PaymentScheduleItems_ToItemId",
                        column: x => x.ToItemId,
                        principalTable: "PaymentScheduleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleAmendments_PaymentSchedules_FromScheduleId",
                        column: x => x.FromScheduleId,
                        principalTable: "PaymentSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleAmendments_PaymentSchedules_ToScheduleId",
                        column: x => x.ToScheduleId,
                        principalTable: "PaymentSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Waivers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgreementId = table.Column<long>(type: "bigint", nullable: false),
                    AmendmentId = table.Column<long>(type: "bigint", nullable: false),
                    ScheduleItemId = table.Column<long>(type: "bigint", nullable: false),
                    AgreementChargeId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DecidedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecisionComments = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
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
                    table.PrimaryKey("PK_Waivers", x => x.Id);
                    table.CheckConstraint("CK_Waivers_Amount", "`Amount` > 0");
                    table.ForeignKey(
                        name: "FK_Waivers_AgreementAmendments_AmendmentId",
                        column: x => x.AmendmentId,
                        principalTable: "AgreementAmendments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Waivers_AgreementCharges_AgreementChargeId",
                        column: x => x.AgreementChargeId,
                        principalTable: "AgreementCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Waivers_CustomerAgreements_AgreementId",
                        column: x => x.AgreementId,
                        principalTable: "CustomerAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Waivers_PaymentScheduleItems_ScheduleItemId",
                        column: x => x.ScheduleItemId,
                        principalTable: "PaymentScheduleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAgreements_InstallmentPlanId",
                table: "CustomerAgreements",
                column: "InstallmentPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementAmendments_BaseScheduleId",
                table: "AgreementAmendments",
                column: "BaseScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementAmendments_IdempotencyKey",
                table: "AgreementAmendments",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgreementAmendments_PendingAgreementId",
                table: "AgreementAmendments",
                column: "PendingAgreementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgreementAmendments_ResultScheduleId",
                table: "AgreementAmendments",
                column: "ResultScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementAmendments_Status_AgreementId",
                table: "AgreementAmendments",
                columns: new[] { "Status", "AgreementId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgreementCharges_AddedByAmendmentId",
                table: "AgreementCharges",
                column: "AddedByAmendmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementCharges_AgreementId_Status_SortOrder",
                table: "AgreementCharges",
                columns: new[] { "AgreementId", "Status", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AgreementCharges_ChargeTypeId",
                table: "AgreementCharges",
                column: "ChargeTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementCharges_LateFeeRuleId",
                table: "AgreementCharges",
                column: "LateFeeRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementCharges_SourcePlanComponentId",
                table: "AgreementCharges",
                column: "SourcePlanComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeTypes_Code",
                table: "ChargeTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlanComponents_ChargeTypeId",
                table: "InstallmentPlanComponents",
                column: "ChargeTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlanComponents_LateFeeRuleId",
                table: "InstallmentPlanComponents",
                column: "LateFeeRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlanComponents_PlanId_SortOrder",
                table: "InstallmentPlanComponents",
                columns: new[] { "PlanId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlans_CustomerId",
                table: "InstallmentPlans",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlans_ProjectId_Code",
                table: "InstallmentPlans",
                columns: new[] { "ProjectId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlans_ProjectId_Status_PlanType",
                table: "InstallmentPlans",
                columns: new[] { "ProjectId", "Status", "PlanType" });

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlans_PropertyCategoryId",
                table: "InstallmentPlans",
                column: "PropertyCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPlans_PropertyId",
                table: "InstallmentPlans",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_LateFeeRules_Code",
                table: "LateFeeRules",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LateFeeRules_ProjectId",
                table: "LateFeeRules",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentScheduleItems_AgreementChargeId",
                table: "PaymentScheduleItems",
                column: "AgreementChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentScheduleItems_DueDate_Status",
                table: "PaymentScheduleItems",
                columns: new[] { "DueDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentScheduleItems_PreviousItemId",
                table: "PaymentScheduleItems",
                column: "PreviousItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentScheduleItems_ScheduleId_LineNo",
                table: "PaymentScheduleItems",
                columns: new[] { "ScheduleId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSchedules_ActiveAgreementId",
                table: "PaymentSchedules",
                column: "ActiveAgreementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSchedules_AgreementId_VersionNo",
                table: "PaymentSchedules",
                columns: new[] { "AgreementId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSchedules_SourceAmendmentId",
                table: "PaymentSchedules",
                column: "SourceAmendmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAmendments_AgreementChargeId",
                table: "ScheduleAmendments",
                column: "AgreementChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAmendments_AmendmentId",
                table: "ScheduleAmendments",
                column: "AmendmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAmendments_FromItemId",
                table: "ScheduleAmendments",
                column: "FromItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAmendments_FromScheduleId",
                table: "ScheduleAmendments",
                column: "FromScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAmendments_ToItemId",
                table: "ScheduleAmendments",
                column: "ToItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleAmendments_ToScheduleId",
                table: "ScheduleAmendments",
                column: "ToScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_Waivers_AgreementChargeId",
                table: "Waivers",
                column: "AgreementChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_Waivers_AgreementId",
                table: "Waivers",
                column: "AgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_Waivers_AmendmentId",
                table: "Waivers",
                column: "AmendmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Waivers_ScheduleItemId",
                table: "Waivers",
                column: "ScheduleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Waivers_Status_AgreementId",
                table: "Waivers",
                columns: new[] { "Status", "AgreementId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgreementAmendments_PaymentSchedules_BaseScheduleId",
                table: "AgreementAmendments",
                column: "BaseScheduleId",
                principalTable: "PaymentSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgreementAmendments_PaymentSchedules_ResultScheduleId",
                table: "AgreementAmendments",
                column: "ResultScheduleId",
                principalTable: "PaymentSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerAgreements_InstallmentPlans_InstallmentPlanId",
                table: "CustomerAgreements",
                column: "InstallmentPlanId",
                principalTable: "InstallmentPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgreementAmendments_PaymentSchedules_BaseScheduleId",
                table: "AgreementAmendments");

            migrationBuilder.DropForeignKey(
                name: "FK_AgreementAmendments_PaymentSchedules_ResultScheduleId",
                table: "AgreementAmendments");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerAgreements_InstallmentPlans_InstallmentPlanId",
                table: "CustomerAgreements");

            migrationBuilder.DropTable(
                name: "ScheduleAmendments");

            migrationBuilder.DropTable(
                name: "Waivers");

            migrationBuilder.DropTable(
                name: "PaymentScheduleItems");

            migrationBuilder.DropTable(
                name: "AgreementCharges");

            migrationBuilder.DropTable(
                name: "PaymentSchedules");

            migrationBuilder.DropTable(
                name: "InstallmentPlanComponents");

            migrationBuilder.DropTable(
                name: "ChargeTypes");

            migrationBuilder.DropTable(
                name: "InstallmentPlans");

            migrationBuilder.DropTable(
                name: "LateFeeRules");

            migrationBuilder.DropIndex(
                name: "IX_CustomerAgreements_InstallmentPlanId",
                table: "CustomerAgreements");

            migrationBuilder.DropIndex(
                name: "IX_AgreementAmendments_BaseScheduleId",
                table: "AgreementAmendments");

            migrationBuilder.DropIndex(
                name: "IX_AgreementAmendments_IdempotencyKey",
                table: "AgreementAmendments");

            migrationBuilder.DropIndex(
                name: "IX_AgreementAmendments_PendingAgreementId",
                table: "AgreementAmendments");

            migrationBuilder.DropIndex(
                name: "IX_AgreementAmendments_ResultScheduleId",
                table: "AgreementAmendments");

            migrationBuilder.DropIndex(
                name: "IX_AgreementAmendments_Status_AgreementId",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "InstallmentPlanId",
                table: "CustomerAgreements");

            migrationBuilder.DropColumn(
                name: "ScheduleStartDate",
                table: "CustomerAgreements");

            migrationBuilder.DropColumn(
                name: "BaseScheduleId",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "DecidedAtUtc",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "DecisionComments",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "PendingAgreementId",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "RequestedByUserId",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "ResultScheduleId",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "AgreementAmendments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "AgreementAmendments");

            migrationBuilder.AlterColumn<int>(
                name: "ToVersionNo",
                table: "AgreementAmendments",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ApprovedByUserId",
                table: "AgreementAmendments",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.UpdateData(
                table: "AgreementAmendments",
                keyColumn: "ApprovedBy",
                keyValue: null,
                column: "ApprovedBy",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "ApprovedBy",
                table: "AgreementAmendments",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
