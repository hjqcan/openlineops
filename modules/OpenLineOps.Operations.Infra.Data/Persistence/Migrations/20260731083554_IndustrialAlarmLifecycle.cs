using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenLineOps.Operations.Infra.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndustrialAlarmLifecycle : Migration
    {
        private static readonly string[] DefinitionStationSourceIndexColumns =
        [
            "StationId",
            "Source"
        ];

        private static readonly string[] FactAlarmVersionIndexColumns =
        [
            "AlarmId",
            "AlarmVersion"
        ];

        private static readonly string[] FactAlarmSequenceIndexColumns =
        [
            "AlarmId",
            "Sequence"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgementComment",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionId",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationAction",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "EscalationDelaySeconds",
                table: "operations_alarms",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLatching",
                table: "operations_alarms",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LastChangedAtUtc",
                table: "operations_alarms",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaximumShelfSeconds",
                table: "operations_alarms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 900);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresBuzzer",
                table: "operations_alarms",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShelfComment",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ShelvedAtUtc",
                table: "operations_alarms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShelvedBy",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ShelvedUntilUtc",
                table: "operations_alarms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SourceActive",
                table: "operations_alarms",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceClearedAtUtc",
                table: "operations_alarms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceClearedBy",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceClearanceNote",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SuppressedAtUtc",
                table: "operations_alarms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuppressedBy",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SuppressedUntilUtc",
                table: "operations_alarms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuppressionReason",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuppressionSource",
                table: "operations_alarms",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "operations_alarms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "operations_alarm_definitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    StationId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsLatching = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresBuzzer = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaximumShelfSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    EscalationDelaySeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    EscalationAction = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    RegistrationCommandId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CommandFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations_alarm_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "operations_alarm_lifecycle_facts",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FactId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    AlarmId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    AlarmVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CommandFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    OccurredAtUtc = table.Column<long>(type: "bigint", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    PreviousSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operations_alarm_lifecycle_facts", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarm_definitions_RegistrationCommandId",
                table: "operations_alarm_definitions",
                column: "RegistrationCommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarm_definitions_StationId_Source",
                table: "operations_alarm_definitions",
                columns: DefinitionStationSourceIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarm_lifecycle_facts_AlarmId_AlarmVersion",
                table: "operations_alarm_lifecycle_facts",
                columns: FactAlarmVersionIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarm_lifecycle_facts_AlarmId_Sequence",
                table: "operations_alarm_lifecycle_facts",
                columns: FactAlarmSequenceIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarm_lifecycle_facts_CommandId",
                table: "operations_alarm_lifecycle_facts",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operations_alarm_lifecycle_facts_FactId",
                table: "operations_alarm_lifecycle_facts",
                column: "FactId",
                unique: true);

            migrationBuilder.Sql(
                """
                UPDATE operations_alarms
                SET "LastChangedAtUtc" = "RaisedAtUtc",
                    "SourceActive" = CASE WHEN "Status" = 'Resolved' THEN 0 ELSE 1 END,
                    "SourceClearedBy" = "ResolvedBy",
                    "SourceClearedAtUtc" = "ResolvedAtUtc",
                    "SourceClearanceNote" = "ResolutionNote",
                    "RequiresBuzzer" = CASE
                        WHEN "Severity" IN ('Major', 'Critical') THEN 1
                        ELSE 0
                    END;

                CREATE TRIGGER operations_alarm_definitions_no_update
                BEFORE UPDATE ON operations_alarm_definitions
                BEGIN
                    SELECT RAISE(ABORT, 'alarm definitions are immutable');
                END;

                CREATE TRIGGER operations_alarm_definitions_no_delete
                BEFORE DELETE ON operations_alarm_definitions
                BEGIN
                    SELECT RAISE(ABORT, 'alarm definitions are immutable');
                END;

                CREATE TRIGGER operations_alarm_lifecycle_facts_no_update
                BEFORE UPDATE ON operations_alarm_lifecycle_facts
                BEGIN
                    SELECT RAISE(ABORT, 'alarm lifecycle facts are append-only');
                END;

                CREATE TRIGGER operations_alarm_lifecycle_facts_no_delete
                BEFORE DELETE ON operations_alarm_lifecycle_facts
                BEGIN
                    SELECT RAISE(ABORT, 'alarm lifecycle facts are append-only');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operations_alarm_definitions");

            migrationBuilder.DropTable(
                name: "operations_alarm_lifecycle_facts");

            migrationBuilder.DropColumn(
                name: "AcknowledgementComment",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "DefinitionId",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "EscalationAction",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "EscalationDelaySeconds",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "IsLatching",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "LastChangedAtUtc",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "MaximumShelfSeconds",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "RequiresBuzzer",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "ShelfComment",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "ShelvedAtUtc",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "ShelvedBy",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "ShelvedUntilUtc",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SourceActive",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SourceClearedAtUtc",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SourceClearedBy",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SourceClearanceNote",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SuppressedAtUtc",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SuppressedBy",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SuppressedUntilUtc",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SuppressionReason",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "SuppressionSource",
                table: "operations_alarms");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "operations_alarms");
        }
    }
}
