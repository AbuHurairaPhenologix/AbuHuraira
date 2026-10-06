using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnomalyDetection.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_version",
                columns: table => new
                {
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    feature_schema_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    training_period_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    training_period_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    random_seed = table.Column<int>(type: "integer", nullable: false),
                    library_versions_json = table.Column<string>(type: "jsonb", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    validation_threshold = table.Column<double>(type: "double precision", nullable: false),
                    threshold_objective = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact_path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    artifact_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    production_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    validation_metrics_json = table.Column<string>(type: "jsonb", nullable: true),
                    trained_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    registered_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    activated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    activated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    deactivated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deactivated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_version", x => x.model_id);
                });

            migrationBuilder.CreateTable(
                name: "quarantined_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    service_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason_codes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    sanitized_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    received_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quarantined_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_definition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_definition", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feature_window",
                columns: table => new
                {
                    window_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    window_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    window_size_minutes = table.Column<int>(type: "integer", nullable: false),
                    feature_schema_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    error_rate = table.Column<double>(type: "double precision", nullable: false),
                    avg_duration_ms = table.Column<double>(type: "double precision", nullable: false),
                    p95duration_ms = table.Column<double>(type: "double precision", nullable: false),
                    auth_failure_rate = table.Column<double>(type: "double precision", nullable: false),
                    dependency_failure_count = table.Column<int>(type: "integer", nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    endpoint_entropy = table.Column<double>(type: "double precision", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    late_event_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    scoring_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scoring_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_scoring_attempt_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_scoring_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_scored_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_window", x => x.window_id);
                    table.ForeignKey(
                        name: "fk_feature_window_services_service_definition_id",
                        column: x => x.service_definition_id,
                        principalTable: "service_definition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operational_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    service_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_timestamp_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    endpoint_group = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<double>(type: "double precision", nullable: true),
                    error_flag = table.Column<bool>(type: "boolean", nullable: false),
                    authentication_result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dependency_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: true),
                    schema_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    received_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processing_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    feature_window_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_late = table.Column<bool>(type: "boolean", nullable: false),
                    indexed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operational_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_operational_event_feature_window_feature_window_id",
                        column: x => x.feature_window_id,
                        principalTable: "feature_window",
                        principalColumn: "window_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_operational_event_services_service_definition_id",
                        column: x => x.service_definition_id,
                        principalTable: "service_definition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scoring_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    feature_schema_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    is_anomaly = table.Column<bool>(type: "boolean", nullable: false),
                    reason_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    reasons_json = table.Column<string>(type: "jsonb", nullable: false),
                    scored_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scoring_record", x => x.id);
                    table.ForeignKey(
                        name: "fk_scoring_record_feature_window_window_id",
                        column: x => x.window_id,
                        principalTable: "feature_window",
                        principalColumn: "window_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scoring_record_model_version_model_id",
                        column: x => x.model_id,
                        principalTable: "model_version",
                        principalColumn: "model_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_record",
                columns: table => new
                {
                    anomaly_id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scoring_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    is_anomaly = table.Column<bool>(type: "boolean", nullable: false),
                    review_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Unreviewed"),
                    reason_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    service_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    window_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_record", x => x.anomaly_id);
                    table.ForeignKey(
                        name: "fk_anomaly_record_feature_window_window_id",
                        column: x => x.window_id,
                        principalTable: "feature_window",
                        principalColumn: "window_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomaly_record_model_version_model_id",
                        column: x => x.model_id,
                        principalTable: "model_version",
                        principalColumn: "model_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_anomaly_record_scoring_records_scoring_record_id",
                        column: x => x.scoring_record_id,
                        principalTable: "scoring_record",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "anomaly_review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    reviewer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anomaly_review", x => x.id);
                    table.ForeignKey(
                        name: "fk_anomaly_review_anomaly_record_anomaly_id",
                        column: x => x.anomaly_id,
                        principalTable: "anomaly_record",
                        principalColumn: "anomaly_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_created_at_utc",
                table: "anomaly_record",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_model_id",
                table: "anomaly_record",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_review_state_created_at_utc",
                table: "anomaly_record",
                columns: new[] { "review_state", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_score",
                table: "anomaly_record",
                column: "score");

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_scoring_record_id",
                table: "anomaly_record",
                column: "scoring_record_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_service_name_environment_window_start_utc",
                table: "anomaly_record",
                columns: new[] { "service_name", "environment", "window_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_record_window_id_model_id",
                table: "anomaly_record",
                columns: new[] { "window_id", "model_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_anomaly_review_anomaly_id_created_at_utc",
                table: "anomaly_review",
                columns: new[] { "anomaly_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_action_occurred_at_utc",
                table: "audit_event",
                columns: new[] { "action", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_event_occurred_at_utc",
                table: "audit_event",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_feature_window_scoring_status_next_scoring_attempt_utc",
                table: "feature_window",
                columns: new[] { "scoring_status", "next_scoring_attempt_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_feature_window_service_definition_id_window_start_utc_windo",
                table: "feature_window",
                columns: new[] { "service_definition_id", "window_start_utc", "window_end_utc", "feature_schema_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_feature_window_service_name_environment_window_start_utc",
                table: "feature_window",
                columns: new[] { "service_name", "environment", "window_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_feature_window_window_start_utc",
                table: "feature_window",
                column: "window_start_utc");

            migrationBuilder.CreateIndex(
                name: "ix_model_version_model_version",
                table: "model_version",
                column: "model_version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_model_version_one_active_per_schema",
                table: "model_version",
                column: "feature_schema_version",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_correlation_id",
                table: "operational_event",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_event_id",
                table: "operational_event",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_feature_window_id",
                table: "operational_event",
                column: "feature_window_id");

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_processing_state_event_timestamp_utc",
                table: "operational_event",
                columns: new[] { "processing_state", "event_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_service_definition_id_event_timestamp_utc",
                table: "operational_event",
                columns: new[] { "service_definition_id", "event_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_service_name_environment_event_timestamp_",
                table: "operational_event",
                columns: new[] { "service_name", "environment", "event_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_operational_event_unindexed",
                table: "operational_event",
                column: "received_at_utc",
                filter: "indexed_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quarantined_event_received_at_utc",
                table: "quarantined_event",
                column: "received_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_scoring_record_model_id_scored_at_utc",
                table: "scoring_record",
                columns: new[] { "model_id", "scored_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_scoring_record_window_id_model_id",
                table: "scoring_record",
                columns: new[] { "window_id", "model_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_definition_name_environment",
                table: "service_definition",
                columns: new[] { "name", "environment" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anomaly_review");

            migrationBuilder.DropTable(
                name: "audit_event");

            migrationBuilder.DropTable(
                name: "operational_event");

            migrationBuilder.DropTable(
                name: "quarantined_event");

            migrationBuilder.DropTable(
                name: "anomaly_record");

            migrationBuilder.DropTable(
                name: "scoring_record");

            migrationBuilder.DropTable(
                name: "feature_window");

            migrationBuilder.DropTable(
                name: "model_version");

            migrationBuilder.DropTable(
                name: "service_definition");
        }
    }
}
