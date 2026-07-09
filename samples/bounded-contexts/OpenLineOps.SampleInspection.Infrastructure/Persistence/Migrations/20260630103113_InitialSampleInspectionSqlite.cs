using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenLineOps.SampleInspection.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSampleInspectionSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inspection_plans",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TargetDeviceId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_plans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_plans_TargetDeviceId",
                table: "inspection_plans",
                column: "TargetDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inspection_plans");
        }
    }
}
