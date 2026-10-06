using System.Buffers.Binary;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.FaultMemory;
using AutoSphere.Diagnostics.Security;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AutoSphere.EcuSimulation.Ecus;

/// <summary>The UDS-inspired diagnostic server of a simulated ECU.</summary>
public abstract partial class EcuSimulator
{
    /// <summary>S3 server timer: a non-default session falls back to default without TesterPresent.</summary>
    private static readonly TimeSpan S3Timeout = TimeSpan.FromSeconds(5);
    private const int MaxSecurityAttempts = 3;

    private readonly object _diagnosticGate = new();
    private DateTimeOffset _lastDiagnosticRequest;
    private byte[]? _pendingSeed;
    private int _failedSecurityAttempts;

    public UdsSession Session { get; private set; } = UdsSession.Default;

    public bool SecurityUnlocked { get; private set; }

    /// <summary>Executes one diagnostic request. Returns <c>null</c> when no response must be sent.</summary>
    public byte[]? HandleDiagnosticRequest(byte[] request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Length == 0)
        {
            return null;
        }

        lock (_diagnosticGate)
        {
            _lastDiagnosticRequest = Context.TimeProvider.GetUtcNow();
            var sid = request[0];
            try
            {
                return (UdsService)sid switch
                {
                    UdsService.DiagnosticSessionControl => HandleSessionControl(request),
                    UdsService.EcuReset => HandleEcuReset(request),
                    UdsService.ClearDiagnosticInformation => HandleClearDtcs(request),
                    UdsService.ReadDtcInformation => HandleReadDtcs(request),
                    UdsService.ReadDataByIdentifier => HandleReadDataByIdentifier(request),
                    UdsService.SecurityAccess => HandleSecurityAccess(request),
                    UdsService.RoutineControl => HandleRoutineControl(request),
                    UdsService.RequestDownload => HandleRequestDownload(request),
                    UdsService.TransferData => HandleTransferData(request),
                    UdsService.RequestTransferExit => HandleRequestTransferExit(request),
                    UdsService.TesterPresent => HandleTesterPresent(request),
                    _ => UdsProtocol.Negative(sid, NegativeResponseCode.ServiceNotSupported),
                };
            }
            catch (IndexOutOfRangeException)
            {
                return UdsProtocol.Negative(sid, NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
            }
            catch (ArgumentOutOfRangeException)
            {
                return UdsProtocol.Negative(sid, NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
            }
        }
    }

    /// <summary>Volatile diagnostic state is lost on reset, like in a real ECU.</summary>
    private void OnReset()
    {
        lock (_diagnosticGate)
        {
            Session = UdsSession.Default;
            SecurityUnlocked = false;
            _pendingSeed = null;
            _failedSecurityAttempts = 0; // the attempt counter is volatile: a power cycle releases the lockout
            ResetProgrammingState();
            ApplyPendingBankSwitch();
        }
    }

    private void ExpireDiagnosticSession()
    {
        lock (_diagnosticGate)
        {
            if (Session != UdsSession.Default && Context.TimeProvider.GetUtcNow() - _lastDiagnosticRequest > S3Timeout)
            {
                Logger.LogDebug("ECU {EcuId}: S3 timeout, returning to default session", EcuId);
                Session = UdsSession.Default;
                SecurityUnlocked = false;
                ResetProgrammingState();
            }
        }
    }

    private async Task DiagnosticServerLoopAsync(CancellationToken cancellationToken)
    {
        var channel = _diagnosticChannel!;
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] request;
            try
            {
                request = await channel.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return;
            }

            if (!IsDiagnosticReachable)
            {
                continue; // a crashed or booting ECU does not answer: the tester will see a P2 timeout
            }

            var response = HandleDiagnosticRequest(request);
            if (response is null)
            {
                continue;
            }

            try
            {
                await channel.SendAsync(response, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "ECU {EcuId} could not send diagnostic response", EcuId);
            }

            if (response[0] == (byte)UdsService.EcuReset + UdsProtocol.PositiveResponseOffset)
            {
                // The positive response is sent before the reset is executed (ISO 14229 behaviour).
                _ = ResetAsync();
            }
        }
    }

    private byte[] HandleSessionControl(byte[] request)
    {
        var requested = (UdsSession)(request[1] & 0x7F);
        if (requested is not (UdsSession.Default or UdsSession.Extended or UdsSession.Programming))
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.SubFunctionNotSupported);
        }

        if (requested == UdsSession.Programming && Session == UdsSession.Default)
        {
            // Programming is entered from the extended session, as in most OEM flash sequences.
            return UdsProtocol.Negative(request[0], NegativeResponseCode.ConditionsNotCorrect);
        }

        if (requested != Session)
        {
            SecurityUnlocked = false;
            ResetProgrammingState();
        }

        Session = requested;

        // P2server = 50 ms, P2*server = 5000 ms (encoded in 10 ms resolution).
        return UdsProtocol.Positive(UdsService.DiagnosticSessionControl, [(byte)requested, 0x00, 0x32, 0x01, 0xF4]);
    }

    private byte[] HandleEcuReset(byte[] request)
    {
        var type = (UdsResetType)(request[1] & 0x7F);
        if (type is not (UdsResetType.HardReset or UdsResetType.KeyOffOnReset or UdsResetType.SoftReset))
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.SubFunctionNotSupported);
        }

        if (_download is { Completed: false })
        {
            // A reset during an active transfer would leave a half-written bank.
            return UdsProtocol.Negative(request[0], NegativeResponseCode.ConditionsNotCorrect);
        }

        return UdsProtocol.Positive(UdsService.EcuReset, [(byte)type]);
    }

    private byte[]? HandleTesterPresent(byte[] request)
    {
        if ((request[1] & 0x7F) != 0x00)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.SubFunctionNotSupported);
        }

        return (request[1] & UdsProtocol.SuppressPositiveResponseBit) != 0
            ? null
            : UdsProtocol.Positive(UdsService.TesterPresent, [0x00]);
    }

    private byte[] HandleReadDataByIdentifier(byte[] request)
    {
        if (request.Length < 3 || (request.Length - 1) % 2 != 0)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
        }

        var response = new List<byte>();
        for (var offset = 1; offset < request.Length; offset += 2)
        {
            var identifier = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(offset));
            if (!DataIdentifierCatalog.TryGet(identifier, out var definition) || !DataIdentifierCatalog.IsSupportedBy(definition, Type))
            {
                return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestOutOfRange);
            }

            response.Add((byte)(identifier >> 8));
            response.Add((byte)identifier);
            response.AddRange(ReadIdentifier(definition));
        }

        return UdsProtocol.Positive(UdsService.ReadDataByIdentifier, response.ToArray());
    }

    private byte[] ReadIdentifier(DataIdentifierDefinition definition)
    {
        if (definition == DataIdentifierCatalog.SoftwareVersion)
        {
            return DataIdentifierCatalog.Encode(definition, ActiveVersion.ToString());
        }

        if (definition == DataIdentifierCatalog.HardwareVersion)
        {
            return DataIdentifierCatalog.Encode(definition, Options.HardwareVersion);
        }

        if (definition == DataIdentifierCatalog.EcuSerialNumber)
        {
            return DataIdentifierCatalog.Encode(definition, Options.SerialNumber);
        }

        if (definition == DataIdentifierCatalog.SparePartNumber)
        {
            return DataIdentifierCatalog.Encode(definition, Options.PartNumber);
        }

        if (definition == DataIdentifierCatalog.Vin)
        {
            return DataIdentifierCatalog.Encode(definition, Context.Vin);
        }

        if (definition == DataIdentifierCatalog.ActiveDiagnosticSession)
        {
            return DataIdentifierCatalog.Encode(definition, (byte)Session);
        }

        return EncodeLiveData(definition);
    }

    private byte[] HandleReadDtcs(byte[] request)
    {
        var subFunction = (ReadDtcSubFunction)(request[1] & 0x7F);
        const byte availabilityMask = 0xFF;
        switch (subFunction)
        {
            case ReadDtcSubFunction.ReportNumberOfDtcByStatusMask:
            {
                var count = Dtcs.Query((DtcStatusBits)request[2]).Count;
                // [subFunction, availabilityMask, DTCFormatIdentifier (0x01 = ISO 14229-1), count high, count low]
                return UdsProtocol.Positive(UdsService.ReadDtcInformation, [(byte)subFunction, availabilityMask, 0x01, (byte)(count >> 8), (byte)count]);
            }

            case ReadDtcSubFunction.ReportDtcByStatusMask:
            {
                var records = Dtcs.Query((DtcStatusBits)request[2]);
                var data = new List<byte> { (byte)subFunction, availabilityMask };
                foreach (var record in records)
                {
                    data.AddRange(UdsCodec.EncodeDtc(record.Code));
                    data.Add((byte)record.Status);
                }

                return UdsProtocol.Positive(UdsService.ReadDtcInformation, data.ToArray());
            }

            case ReadDtcSubFunction.ReportDtcSnapshotRecordByDtcNumber:
            {
                if (request.Length < 6)
                {
                    return UdsProtocol.Negative(request[0], NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
                }

                var code = DtcCode.FromUdsDtc(UdsCodec.ReadDtc(request.AsSpan(2)));
                if (!Dtcs.TryGetSnapshot(code, out var status, out var snapshot))
                {
                    return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestOutOfRange);
                }

                return UdsProtocol.Positive(UdsService.ReadDtcInformation, UdsCodec.EncodeSnapshot(code, status, 0x01, snapshot));
            }

            default:
                return UdsProtocol.Negative(request[0], NegativeResponseCode.SubFunctionNotSupported);
        }
    }

    private byte[] HandleClearDtcs(byte[] request)
    {
        if (request.Length != 4)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
        }

        if (Session == UdsSession.Programming)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.ServiceNotSupportedInActiveSession);
        }

        var group = UdsCodec.ReadDtc(request.AsSpan(1));
        var result = Dtcs.Clear(group == UdsProtocol.AllDtcGroups ? null : DtcCode.FromUdsDtc(group));
        return result switch
        {
            DtcClearResult.ConditionStillPresent => UdsProtocol.Negative(request[0], NegativeResponseCode.ConditionsNotCorrect),
            DtcClearResult.NotStored when group != UdsProtocol.AllDtcGroups => UdsProtocol.Negative(request[0], NegativeResponseCode.RequestOutOfRange),
            _ => UdsProtocol.Positive(UdsService.ClearDiagnosticInformation, []),
        };
    }

    private byte[] HandleSecurityAccess(byte[] request)
    {
        if (Session == UdsSession.Default)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.ServiceNotSupportedInActiveSession);
        }

        if (_failedSecurityAttempts >= MaxSecurityAttempts)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.ExceededNumberOfAttempts);
        }

        var subFunction = request[1];
        if (subFunction == UdsProtocol.ProgrammingSecurityLevel)
        {
            if (SecurityUnlocked)
            {
                return UdsProtocol.Positive(UdsService.SecurityAccess, [subFunction, 0, 0, 0, 0]); // zero seed: already unlocked
            }

            _pendingSeed = SecurityAccessAlgorithm.GenerateSeed();
            return UdsProtocol.Positive(UdsService.SecurityAccess, [subFunction, .. _pendingSeed]);
        }

        if (subFunction == UdsProtocol.ProgrammingSecurityLevel + 1)
        {
            if (_pendingSeed is null)
            {
                return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestSequenceError);
            }

            var valid = SecurityAccessAlgorithm.KeyIsValid(_pendingSeed, request.AsSpan(2), Context.SecurityAccessSecret);
            _pendingSeed = null;
            if (!valid)
            {
                _failedSecurityAttempts++;
                return UdsProtocol.Negative(request[0], NegativeResponseCode.InvalidKey);
            }

            _failedSecurityAttempts = 0;
            SecurityUnlocked = true;
            return UdsProtocol.Positive(UdsService.SecurityAccess, [subFunction]);
        }

        return UdsProtocol.Negative(request[0], NegativeResponseCode.SubFunctionNotSupported);
    }
}
