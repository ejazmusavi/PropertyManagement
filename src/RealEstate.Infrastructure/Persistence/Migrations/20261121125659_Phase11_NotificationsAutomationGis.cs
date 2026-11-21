using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11_NotificationsAutomationGis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundJobRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    JobKey = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TriggeredBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ItemsProcessed = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobRuns", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    OwnerKey = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TypeKey = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
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
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationPreferences_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    RecipientUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    AgreementId = table.Column<long>(type: "bigint", nullable: true),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Body = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DataJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RelatedType = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RelatedId = table.Column<long>(type: "bigint", nullable: true),
                    DedupeKey = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    CancelReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
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
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NotificationTemplates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Language = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Subject = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Body = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
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
                    table.PrimaryKey("PK_NotificationTemplates", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SpatialReferenceSystems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Srid = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Unit = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsSystem = table.Column<bool>(type: "tinyint(1)", nullable: false),
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
                    table.PrimaryKey("PK_SpatialReferenceSystems", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NotificationDeliveryLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    NotificationId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Recipient = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Subject = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Body = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ProviderReference = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastError = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveryLogs", x => x.Id);
                    table.CheckConstraint("CK_NotificationDeliveryLogs_Attempts", "`Attempts` >= 0 AND `Attempts` <= `MaxAttempts` + 1");
                    table.ForeignKey(
                        name: "FK_NotificationDeliveryLogs_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MapDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SrsId = table.Column<long>(type: "bigint", nullable: true),
                    SurveyReference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StorageKey = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentType = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DeletedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeleteReason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MapDocuments_SpatialReferenceSystems_SrsId",
                        column: x => x.SrsId,
                        principalTable: "SpatialReferenceSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MapLayers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SrsId = table.Column<long>(type: "bigint", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsVisibleByDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    StyleJson = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
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
                    table.PrimaryKey("PK_MapLayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapLayers_MapDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "MapDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MapLayers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MapLayers_SpatialReferenceSystems_SrsId",
                        column: x => x.SrsId,
                        principalTable: "SpatialReferenceSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ProjectBoundaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    ActiveProjectId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    SrsId = table.Column<long>(type: "bigint", nullable: false),
                    GeometryType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeometryJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VertexCount = table.Column<int>(type: "int", nullable: false),
                    MinX = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MinY = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxX = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxY = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyReference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyedBy = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    Remarks = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SubmittedByUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    VerifiedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    VerifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecisionComment = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SupersededAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectBoundaries", x => x.Id);
                    table.CheckConstraint("CK_ProjectBoundaries_Active", "(`Status` = 'Verified' AND `ActiveProjectId` IS NOT NULL) OR (`Status` <> 'Verified' AND `ActiveProjectId` IS NULL)");
                    table.ForeignKey(
                        name: "FK_ProjectBoundaries_MapDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "MapDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectBoundaries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectBoundaries_SpatialReferenceSystems_SrsId",
                        column: x => x.SrsId,
                        principalTable: "SpatialReferenceSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PropertyBoundaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PropertyId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    ActivePropertyId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    SrsId = table.Column<long>(type: "bigint", nullable: false),
                    GeometryType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeometryJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VertexCount = table.Column<int>(type: "int", nullable: false),
                    MinX = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MinY = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxX = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MaxY = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyReference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyedBy = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SurveyDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    Remarks = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SubmittedByUserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    VerifiedByUserId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    VerifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecisionComment = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SupersededAtUtc = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    RowVersion = table.Column<DateTime>(type: "timestamp(6)", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyBoundaries", x => x.Id);
                    table.CheckConstraint("CK_PropertyBoundaries_Active", "(`Status` = 'Verified' AND `ActivePropertyId` IS NOT NULL) OR (`Status` <> 'Verified' AND `ActivePropertyId` IS NULL)");
                    table.ForeignKey(
                        name: "FK_PropertyBoundaries_MapDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "MapDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyBoundaries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyBoundaries_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyBoundaries_SpatialReferenceSystems_SrsId",
                        column: x => x.SrsId,
                        principalTable: "SpatialReferenceSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobRuns_JobKey_StartedAtUtc",
                table: "BackgroundJobRuns",
                columns: new[] { "JobKey", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MapDocuments_ProjectId",
                table: "MapDocuments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MapDocuments_SrsId",
                table: "MapDocuments",
                column: "SrsId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLayers_ProjectId_Code",
                table: "MapLayers",
                columns: new[] { "ProjectId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapLayers_SourceDocumentId",
                table: "MapLayers",
                column: "SourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLayers_SrsId",
                table: "MapLayers",
                column: "SrsId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryLogs_NotificationId_Channel",
                table: "NotificationDeliveryLogs",
                columns: new[] { "NotificationId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveryLogs_Status_NextAttemptAtUtc",
                table: "NotificationDeliveryLogs",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_CustomerId",
                table: "NotificationPreferences",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_OwnerKey_Channel_TypeKey",
                table: "NotificationPreferences",
                columns: new[] { "OwnerKey", "Channel", "TypeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_TypeKey_IsEnabled",
                table: "NotificationPreferences",
                columns: new[] { "TypeKey", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CustomerId_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "CustomerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DedupeKey",
                table: "Notifications",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ProjectId",
                table: "Notifications",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUserId_ReadAtUtc",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Status_Type",
                table: "Notifications",
                columns: new[] { "Status", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationTemplates_Type_Channel_Language",
                table: "NotificationTemplates",
                columns: new[] { "Type", "Channel", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBoundaries_ActiveProjectId",
                table: "ProjectBoundaries",
                column: "ActiveProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBoundaries_ProjectId_VersionNo",
                table: "ProjectBoundaries",
                columns: new[] { "ProjectId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBoundaries_SourceDocumentId",
                table: "ProjectBoundaries",
                column: "SourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBoundaries_SrsId",
                table: "ProjectBoundaries",
                column: "SrsId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyBoundaries_ActivePropertyId",
                table: "PropertyBoundaries",
                column: "ActivePropertyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyBoundaries_ProjectId_Status",
                table: "PropertyBoundaries",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyBoundaries_PropertyId_VersionNo",
                table: "PropertyBoundaries",
                columns: new[] { "PropertyId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyBoundaries_SourceDocumentId",
                table: "PropertyBoundaries",
                column: "SourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyBoundaries_SrsId",
                table: "PropertyBoundaries",
                column: "SrsId");

            migrationBuilder.CreateIndex(
                name: "IX_SpatialReferenceSystems_Code",
                table: "SpatialReferenceSystems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpatialReferenceSystems_Srid",
                table: "SpatialReferenceSystems",
                column: "Srid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundJobRuns");

            migrationBuilder.DropTable(
                name: "MapLayers");

            migrationBuilder.DropTable(
                name: "NotificationDeliveryLogs");

            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropTable(
                name: "NotificationTemplates");

            migrationBuilder.DropTable(
                name: "ProjectBoundaries");

            migrationBuilder.DropTable(
                name: "PropertyBoundaries");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "MapDocuments");

            migrationBuilder.DropTable(
                name: "SpatialReferenceSystems");
        }
    }
}
