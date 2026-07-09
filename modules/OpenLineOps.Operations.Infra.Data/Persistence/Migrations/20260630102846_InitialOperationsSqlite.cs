using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenLineOps.Operations.Infra.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOperationsSqlite : Migration
    {
        private static readonly string[] AlarmStationStatusIndexColumns =
        [
            "StationId",
            "Status"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operations_alarms",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    StationId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    RaisedAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AcknowledgedAtUtc = table.Column<long>(type: "bigint", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ResolvedAtUtc = table.Column<long>(type: "bigint", nullable: true),
                    ResolutionNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations_alarms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarms_RaisedAtUtc",
                table: "operations_alarms",
                column: "RaisedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarms_StationId_Status",
                table: "operations_alarms",
                columns: AlarmStationStatusIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operations_alarms");
        }
    }
}
