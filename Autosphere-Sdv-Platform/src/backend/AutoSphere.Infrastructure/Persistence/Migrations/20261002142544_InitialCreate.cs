using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AutoSphere.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "software_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_ecu_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    minimum_compatible_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    payload_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    payload_size = table.Column<long>(type: "bigint", nullable: false),
                    signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    signing_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    release_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    firmware_description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_packages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model_year = table.Column<int>(type: "integer", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    connectivity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    connectivity_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    gateway_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    gateway_software_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    simulation_mode = table.Column<bool>(type: "boolean", nullable: false),
                    health = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    health_score = table.Column<int>(type: "integer", nullable: false),
                    health_summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    health_evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<string>(type: "text", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "AspNetRoles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_user_claims_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_asp_net_user_logins_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "AspNetRoles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ota_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ota_campaigns", x => x.id);
                    table.ForeignKey(
                        name: "fk_ota_campaigns_software_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "software_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ecu_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    signal_path = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    value = table.Column<double>(type: "double precision", nullable: true),
                    threshold = table.Column<double>(type: "double precision", nullable: true),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ecu_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    vehicle_duration_ms = table.Column<double>(type: "double precision", nullable: true),
                    round_trip_ms = table.Column<double>(type: "double precision", nullable: true),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    diagnosis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_diagnostic_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_diagnostic_sessions_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_trouble_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    ecu_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character(5)", fixedLength: true, maxLength: 5, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fault_category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_mask = table.Column<byte>(type: "smallint", nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    snapshot_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_diagnostic_trouble_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_diagnostic_trouble_codes_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ecus",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    ecu_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    software_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    hardware_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active_dtc_count = table.Column<int>(type: "integer", nullable: false),
                    timeout_count = table.Column<long>(type: "bigint", nullable: false),
                    e2e_error_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ecus", x => x.id);
                    table.ForeignKey(
                        name: "fk_ecus_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fault_injections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    fault = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_ecu_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    requested_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted = table.Column<bool>(type: "boolean", nullable: true),
                    result = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    handled_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fault_injections", x => x.id);
                    table.ForeignKey(
                        name: "fk_fault_injections_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    signal_path = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_telemetry_records_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vehicle_health_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicle_health_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_vehicle_health_snapshots_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ota_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_key = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ecu_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    to_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    installed_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    last_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    simulate_transport_corruption = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ota_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_ota_deployments_ota_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "ota_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ota_deployments_software_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "software_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ota_deployments_vehicles_vehicle_key",
                        column: x => x.vehicle_key,
                        principalTable: "vehicles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ota_deployment_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ota_deployment_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_ota_deployment_events_ota_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "ota_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_vehicle_key_alert_key_cleared_at",
                table: "alerts",
                columns: new[] { "vehicle_key", "alert_key", "cleared_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_vehicle_key_raised_at",
                table: "alerts",
                columns: new[] { "vehicle_key", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_role_claims_role_id",
                table: "AspNetRoleClaims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_claims_user_id",
                table: "AspNetUserClaims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_logins_user_id",
                table: "AspNetUserLogins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_roles_role_id",
                table: "AspNetUserRoles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_diagnostic_sessions_vehicle_key_requested_at",
                table: "diagnostic_sessions",
                columns: new[] { "vehicle_key", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_diagnostic_trouble_codes_vehicle_key_ecu_id_code",
                table: "diagnostic_trouble_codes",
                columns: new[] { "vehicle_key", "ecu_id", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_diagnostic_trouble_codes_vehicle_key_status",
                table: "diagnostic_trouble_codes",
                columns: new[] { "vehicle_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ecus_vehicle_key_ecu_id",
                table: "ecus",
                columns: new[] { "vehicle_key", "ecu_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fault_injections_vehicle_key_requested_at",
                table: "fault_injections",
                columns: new[] { "vehicle_key", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ota_campaigns_package_id",
                table: "ota_campaigns",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_ota_deployment_events_deployment_id_timestamp",
                table: "ota_deployment_events",
                columns: new[] { "deployment_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_ota_deployments_campaign_id",
                table: "ota_deployments",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_ota_deployments_package_id",
                table: "ota_deployments",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_ota_deployments_status",
                table: "ota_deployments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ota_deployments_vehicle_key_created_at",
                table: "ota_deployments",
                columns: new[] { "vehicle_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_software_packages_target_ecu_type_version",
                table: "software_packages",
                columns: new[] { "target_ecu_type", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_records_timestamp",
                table: "telemetry_records",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_records_vehicle_key_signal_path_timestamp",
                table: "telemetry_records",
                columns: new[] { "vehicle_key", "signal_path", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_health_snapshots_vehicle_key_timestamp",
                table: "vehicle_health_snapshots",
                columns: new[] { "vehicle_key", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_vehicles_vehicle_id",
                table: "vehicles",
                column: "vehicle_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicles_vin",
                table: "vehicles",
                column: "vin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "diagnostic_sessions");

            migrationBuilder.DropTable(
                name: "diagnostic_trouble_codes");

            migrationBuilder.DropTable(
                name: "ecus");

            migrationBuilder.DropTable(
                name: "fault_injections");

            migrationBuilder.DropTable(
                name: "ota_deployment_events");

            migrationBuilder.DropTable(
                name: "telemetry_records");

            migrationBuilder.DropTable(
                name: "vehicle_health_snapshots");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "ota_deployments");

            migrationBuilder.DropTable(
                name: "ota_campaigns");

            migrationBuilder.DropTable(
                name: "vehicles");

            migrationBuilder.DropTable(
                name: "software_packages");
        }
    }
}
