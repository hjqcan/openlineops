using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef.Migrations
{
    /// <inheritdoc />
    public partial class InitialDevicesEfSqlite : Migration
    {
        private static readonly string[] DeviceInstanceDefinitionStatusIndexColumns =
        [
            "DefinitionId",
            "Status"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_definitions_ef",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_definitions_ef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "device_instances_ef",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    StationId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    endpoint_protocol = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    endpoint_address = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConnectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastDisconnectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FaultReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_instances_ef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "device_definition_capabilities_ef",
                columns: table => new
                {
                    capability_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_definition_capabilities_ef", x => new { x.DefinitionId, x.capability_id });
                    table.ForeignKey(
                        name: "FK_device_definition_capabilities_ef_device_definitions_ef_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "device_definitions_ef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_definition_commands_ef",
                columns: table => new
                {
                    command_definition_id = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    capability_id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CommandName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    InputSchema = table.Column<string>(type: "TEXT", nullable: true),
                    OutputSchema = table.Column<string>(type: "TEXT", nullable: true),
                    Timeout = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_definition_commands_ef", x => new { x.DefinitionId, x.command_definition_id });
                    table.ForeignKey(
                        name: "FK_device_definition_commands_ef_device_definitions_ef_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "device_definitions_ef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_definitions_ef_PluginId",
                table: "device_definitions_ef",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_device_instances_ef_DefinitionId_Status",
                table: "device_instances_ef",
                columns: DeviceInstanceDefinitionStatusIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_device_instances_ef_StationId",
                table: "device_instances_ef",
                column: "StationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_definition_capabilities_ef");

            migrationBuilder.DropTable(
                name: "device_definition_commands_ef");

            migrationBuilder.DropTable(
                name: "device_instances_ef");

            migrationBuilder.DropTable(
                name: "device_definitions_ef");
        }
    }
}
