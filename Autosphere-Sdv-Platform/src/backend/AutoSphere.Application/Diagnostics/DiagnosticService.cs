using System.Text.Json;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.Domain.Common;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Vehicles;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Diagnostics;

public sealed class DiagnosticRequestValidator : AbstractValidator<DiagnosticRequest>
{
    public DiagnosticRequestValidator()
    {
        RuleFor(r => r.Operation).IsInEnum();
        RuleFor(r => r.EcuId).NotEmpty()
            .When(r => r.Operation is DiagnosticOperation.EcuReset or DiagnosticOperation.ReadDataByIdentifier or DiagnosticOperation.SessionControl)
            .WithMessage("This operation requires a target ECU.");
        RuleFor(r => r.DtcCode).Must(c => DtcCode.TryParse(c, out _)).When(r => r.DtcCode is not null).WithMessage("Invalid DTC code.");
        RuleFor(r => r.DataIdentifiers).NotEmpty().When(r => r.Operation == DiagnosticOperation.ReadDataByIdentifier);
        RuleFor(r => r.DataIdentifiers!.Count).LessThanOrEqualTo(16).When(r => r.DataIdentifiers is not null);
        RuleFor(r => r.Session).NotNull().When(r => r.Operation == DiagnosticOperation.SessionControl);
    }
}

/// <summary>Remote diagnostics use cases (request/response over MQTT) and DTC queries.</summary>
public sealed class DiagnosticService(
    IAutoSphereDbContext db,
    VehicleService vehicles,
    IVehicleCommandPublisher publisher,
    DiagnosticResponseAwaiter awaiter,
    IRealtimeNotifier notifier,
    ICurrentUser currentUser,
    IValidator<DiagnosticRequest> validator,
    IOptions<DiagnosticsOptions> options,
    TimeProvider timeProvider,
    ILogger<DiagnosticService> logger)
{
    /// <summary>Sends a diagnostic request and waits for the vehicle's correlated response.</summary>
    public async Task<DiagnosticSessionDto> ExecuteAsync(string vehicleId, DiagnosticRequest request, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var vehicle = await vehicles.LoadAsync(vehicleId, tracking: false, cancellationToken);
        if (request.EcuId is not null)
        {
            VehicleService.RequireEcu(vehicle, request.EcuId);
        }

        if (vehicle.Connectivity != ConnectivityStatus.Online)
        {
            throw new DomainException($"Vehicle {vehicle.VehicleId} is offline; diagnostics require a connected gateway.");
        }

        var correlationId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var session = DiagnosticSession.Start(correlationId, vehicle.Id, request.Operation.ToString(), request.EcuId, currentUser.UserName, now);
        db.DiagnosticSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["VehicleId"] = vehicle.VehicleId });
        var response = awaiter.Register(correlationId);
        await publisher.PublishDiagnosticRequestAsync(new DiagnosticRequestMessage
        {
            VehicleId = vehicle.VehicleId,
            Timestamp = now,
            CorrelationId = correlationId,
            Operation = request.Operation,
            EcuId = request.EcuId,
            DtcCode = request.DtcCode,
            DataIdentifiers = request.DataIdentifiers,
            Session = request.Session,
            ResetKind = request.ResetKind,
            RequestedBy = currentUser.UserName,
        }, cancellationToken);
        logger.LogInformation("Diagnostic request {Operation} sent to {VehicleId}/{EcuId}", request.Operation, vehicle.VehicleId, request.EcuId ?? "*");

        var timeout = Task.Delay(TimeSpan.FromSeconds(options.Value.ResponseTimeoutSeconds), timeProvider, cancellationToken);
        if (await Task.WhenAny(response, timeout) == response)
        {
            return await response;
        }

        awaiter.Abandon(correlationId);

        // Read the persisted state: a late response may have completed the session from another scope.
        var current = await db.DiagnosticSessions.AsNoTracking().FirstAsync(s => s.Id == correlationId, cancellationToken);
        if (current.Status != DiagnosticSessionStatus.Pending)
        {
            return ToDto(current, vehicle.VehicleId);
        }

        session.TimeOut(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Diagnostic request {Operation} to {VehicleId} timed out", request.Operation, vehicle.VehicleId);
        return ToDto(session, vehicle.VehicleId);
    }

    /// <summary>Clears DTCs whose fault condition is no longer present.</summary>
    public async Task<DiagnosticSessionDto> ClearAsync(string vehicleId, ClearDtcsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vehicle = await vehicles.LoadAsync(vehicleId, tracking: false, cancellationToken);
        var stored = await db.DiagnosticTroubleCodes.AsNoTracking()
            .Where(d => d.VehicleKey == vehicle.Id && d.Status != DtcRecordStatus.Cleared)
            .ToListAsync(cancellationToken);

        if (request.Code is not null)
        {
            var dtc = stored.FirstOrDefault(d => string.Equals(d.Code, request.Code, StringComparison.OrdinalIgnoreCase)
                                                 && (request.EcuId is null || string.Equals(d.EcuId, request.EcuId, StringComparison.OrdinalIgnoreCase)))
                      ?? throw NotFoundException.For("DTC", request.Code);
            if (!dtc.IsClearable)
            {
                throw new DomainException($"{dtc.Code} on {dtc.EcuId} is still active. Resolve the underlying fault before clearing it.");
            }

            return await ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.ClearDtcs, dtc.EcuId, dtc.Code), cancellationToken);
        }

        var targets = stored.Where(d => d.IsClearable && (request.EcuId is null || d.EcuId == request.EcuId)).Select(d => d.EcuId).Distinct().ToList();
        if (targets.Count == 0)
        {
            throw new DomainException("There are no clearable DTCs: active DTCs can only be cleared after their fault condition is resolved.");
        }

        DiagnosticSessionDto? last = null;
        foreach (var ecuId in targets)
        {
            last = await ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.ClearDtcs, ecuId), cancellationToken);
        }

        return last!;
    }

    /// <summary>Handles a diagnostic response published by the vehicle.</summary>
    public async Task HandleResponseAsync(DiagnosticResponseMessage message, Guid vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var session = await db.DiagnosticSessions.FirstOrDefaultAsync(s => s.Id == message.CorrelationId && s.VehicleKey == vehicleKey, cancellationToken);
        if (session is null)
        {
            logger.LogWarning("Diagnostic response with unknown correlation id {CorrelationId} ignored", message.CorrelationId);
            return;
        }

        session.Complete(message.Success, message.Error, message.TotalDurationMs, JsonSerializer.Serialize(message.Results, AutoSphereJson.Options),
            Diagnose(message), timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        var dto = ToDto(session, message.VehicleId, message.Results);
        logger.LogInformation("Diagnostic {Operation} for {VehicleId} completed: {Status}, vehicle {VehicleMs} ms, round trip {RoundTripMs} ms. {Diagnosis}",
            session.Operation, message.VehicleId, session.Status, message.TotalDurationMs, Math.Round(session.RoundTripMs ?? 0, 1), session.Diagnosis);
        awaiter.TryComplete(message.CorrelationId, dto);
        await notifier.DiagnosticCompletedAsync(message.VehicleId, dto, cancellationToken);
    }

    public async Task<IReadOnlyList<DtcRecordDto>> GetDtcsAsync(string vehicleId, bool includeHistory, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.LoadAsync(vehicleId, tracking: false, cancellationToken);
        var query = db.DiagnosticTroubleCodes.AsNoTracking().Where(d => d.VehicleKey == vehicle.Id);
        if (!includeHistory)
        {
            query = query.Where(d => d.Status != DtcRecordStatus.Cleared);
        }

        var dtcs = await query.OrderBy(d => d.Status).ThenByDescending(d => d.DetectedAt).Take(500).ToListAsync(cancellationToken);
        return dtcs.Select(d => d.ToDto()).ToList();
    }

    public async Task<IReadOnlyList<DiagnosticSessionDto>> GetHistoryAsync(string vehicleId, int take, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.LoadAsync(vehicleId, tracking: false, cancellationToken);
        var sessions = await db.DiagnosticSessions.AsNoTracking().Where(s => s.VehicleKey == vehicle.Id)
            .OrderByDescending(s => s.RequestedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(cancellationToken);
        return sessions.Select(s => ToDto(s, vehicle.VehicleId)).ToList();
    }

    public async Task<DiagnosticSessionDto> GetSessionAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var session = await db.DiagnosticSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == correlationId, cancellationToken)
                      ?? throw NotFoundException.For("Diagnostic session", correlationId);
        var vehicleId = await db.Vehicles.Where(v => v.Id == session.VehicleKey).Select(v => v.VehicleId).FirstAsync(cancellationToken);
        return ToDto(session, vehicleId);
    }

    /// <summary>Turns raw results into an engineer-readable diagnosis.</summary>
    public static string Diagnose(DiagnosticResponseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var dtcs = message.Results.SelectMany(r => r.Dtcs ?? []).ToList();
        var unreachable = message.Results.Where(r => !r.Success).Select(r => r.EcuId).ToList();
        var findings = dtcs.Where(d => d.TestFailed)
            .OrderByDescending(d => d.Severity)
            .Select(d => $"{d.FaultCategory} ({d.Code} on {d.EcuId})")
            .Distinct()
            .ToList();
        if (message.Operation is not (DiagnosticOperation.FullScan or DiagnosticOperation.ReadDtcs))
        {
            return message.Success ? $"{message.Operation} completed." : $"{message.Operation} failed: {message.Error}";
        }

        var text = findings.Count == 0 ? "No active faults detected." : "Active faults: " + string.Join("; ", findings) + ".";
        var stored = dtcs.Count(d => !d.TestFailed);
        if (stored > 0)
        {
            text += $" {stored} stored (healed) DTC(s) can be cleared.";
        }

        if (unreachable.Count > 0)
        {
            text += $" Not responding: {string.Join(", ", unreachable)}.";
        }

        return text;
    }

    private static DiagnosticSessionDto ToDto(DiagnosticSession session, string vehicleId, IReadOnlyList<EcuDiagnosticResultDto>? results = null)
    {
        results ??= session.ResultJson is null
            ? []
            : JsonSerializer.Deserialize<List<EcuDiagnosticResultDto>>(session.ResultJson, AutoSphereJson.Options) ?? [];
        return new DiagnosticSessionDto(session.Id, vehicleId, session.Operation, session.EcuId, session.Status, session.RequestedBy, session.RequestedAt,
            session.CompletedAt, session.VehicleDurationMs, session.RoundTripMs, session.Error, session.Diagnosis, results);
    }
}
