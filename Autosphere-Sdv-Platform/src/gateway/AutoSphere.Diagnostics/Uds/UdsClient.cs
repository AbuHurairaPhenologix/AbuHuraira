using System.Buffers.Binary;
using System.Diagnostics;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.Security;
using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Diagnostics.Uds;

public sealed record UdsClientOptions
{
    /// <summary>P2 client: maximum time until the first response.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>P2* client: extended timeout after a responsePending (0x78) NRC.</summary>
    public TimeSpan ExtendedResponseTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>The diagnostic operations a tester can perform on one ECU.</summary>
public interface IUdsClient
{
    Task<UdsResponse> RequestAsync(byte[] request, UdsTrace? trace, CancellationToken cancellationToken);
}

/// <summary>
/// UDS-inspired diagnostic client (tester) for one ECU. Requests are strictly sequential, as on a real
/// physical diagnostic channel. Negative responses are surfaced as <see cref="UdsNegativeResponseException"/>.
/// </summary>
public sealed class UdsClient : IUdsClient, IAsyncDisposable
{
    private readonly IsoTpChannel _transport;
    private readonly UdsClientOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public UdsClient(IsoTpChannel transport, UdsClientOptions? options = null)
    {
        _transport = transport;
        _options = options ?? new UdsClientOptions();
    }

    public async Task<UdsResponse> RequestAsync(byte[] request, UdsTrace? trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = (UdsService)request[0];
        var suppressResponse = request.Length > 1 && HasSubFunction(service) && (request[1] & UdsProtocol.SuppressPositiveResponseBit) != 0;

        await _lock.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _transport.DiscardPendingMessages();
            await _transport.SendAsync(request, cancellationToken);
            if (suppressResponse)
            {
                trace?.Add(new UdsExchange(service, request, null, stopwatch.Elapsed, null));
                return new UdsResponse([(byte)(request[0] + UdsProtocol.PositiveResponseOffset)]);
            }

            var timeout = _options.ResponseTimeout;
            while (true)
            {
                var raw = await _transport.ReceiveAsync(timeout, cancellationToken)
                          ?? throw Record(new UdsTimeoutException(service, timeout), trace, service, request, null, stopwatch);
                var response = new UdsResponse(raw);
                if (response.ServiceId != request[0])
                {
                    continue; // stale response for another service
                }

                if (response.NegativeResponseCode == NegativeResponseCode.RequestCorrectlyReceivedResponsePending)
                {
                    timeout = _options.ExtendedResponseTimeout;
                    continue;
                }

                trace?.Add(new UdsExchange(service, request, raw, stopwatch.Elapsed, response.NegativeResponseCode));
                if (response.IsNegative)
                {
                    throw new UdsNegativeResponseException(service, response.NegativeResponseCode ?? NegativeResponseCode.GeneralReject);
                }

                return response;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<UdsSession> StartSessionAsync(UdsSession session, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var response = await RequestAsync([(byte)UdsService.DiagnosticSessionControl, (byte)session], trace, cancellationToken);
        return (UdsSession)response.Data[0];
    }

    public Task TesterPresentAsync(UdsTrace? trace, CancellationToken cancellationToken, bool suppressResponse = false) =>
        RequestAsync([(byte)UdsService.TesterPresent, suppressResponse ? UdsProtocol.SuppressPositiveResponseBit : (byte)0x00], trace, cancellationToken);

    public Task EcuResetAsync(UdsResetType resetType, UdsTrace? trace, CancellationToken cancellationToken) =>
        RequestAsync([(byte)UdsService.EcuReset, (byte)resetType], trace, cancellationToken);

    public async Task<(DataIdentifierDefinition Definition, string Text, double? Numeric)> ReadDataByIdentifierAsync(
        DataIdentifierDefinition definition, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            [(byte)UdsService.ReadDataByIdentifier, (byte)(definition.Identifier >> 8), (byte)definition.Identifier], trace, cancellationToken);
        var data = response.Data;
        if (data.Length < 2 + definition.Length || BinaryPrimitives.ReadUInt16BigEndian(data) != definition.Identifier)
        {
            throw new UdsException($"Malformed ReadDataByIdentifier response for DID {definition.Hex}.");
        }

        var (text, numeric) = DataIdentifierCatalog.Decode(definition, data[2..]);
        return (definition, text, numeric);
    }

    public async Task<IReadOnlyList<UdsDtcRecord>> ReadDtcsAsync(DtcStatusBits statusMask, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            [(byte)UdsService.ReadDtcInformation, (byte)ReadDtcSubFunction.ReportDtcByStatusMask, (byte)statusMask], trace, cancellationToken);
        return UdsCodec.ParseDtcsByStatusMask(response.Data);
    }

    public async Task<IReadOnlyList<UdsSnapshotValue>> ReadDtcSnapshotAsync(DtcCode code, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var dtc = UdsCodec.EncodeDtc(code);
        var response = await RequestAsync(
            [(byte)UdsService.ReadDtcInformation, (byte)ReadDtcSubFunction.ReportDtcSnapshotRecordByDtcNumber, dtc[0], dtc[1], dtc[2], 0xFF],
            trace, cancellationToken);
        return UdsCodec.ParseSnapshot(response.Data);
    }

    /// <summary>Clears all DTCs (<paramref name="code"/> = null) or a single DTC.</summary>
    public Task ClearDtcsAsync(DtcCode? code, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var group = code is null ? [0xFF, 0xFF, 0xFF] : UdsCodec.EncodeDtc(code.Value);
        return RequestAsync([(byte)UdsService.ClearDiagnosticInformation, group[0], group[1], group[2]], trace, cancellationToken);
    }

    /// <summary>Performs the two-step seed/key exchange.</summary>
    public async Task UnlockAsync(string sharedSecret, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var seedResponse = await RequestAsync([(byte)UdsService.SecurityAccess, UdsProtocol.ProgrammingSecurityLevel], trace, cancellationToken);
        var seed = seedResponse.Data[1..].ToArray();
        if (seed.All(b => b == 0))
        {
            return; // already unlocked
        }

        var key = SecurityAccessAlgorithm.ComputeKey(seed, sharedSecret);
        await RequestAsync([(byte)UdsService.SecurityAccess, (byte)(UdsProtocol.ProgrammingSecurityLevel + 1), .. key], trace, cancellationToken);
    }

    public async Task<byte[]> RoutineControlAsync(ushort routineId, byte[]? options, UdsTrace? trace, CancellationToken cancellationToken)
    {
        byte[] request = [(byte)UdsService.RoutineControl, (byte)RoutineControlType.StartRoutine, (byte)(routineId >> 8), (byte)routineId, .. options ?? []];
        var response = await RequestAsync(request, trace, cancellationToken);
        return response.Data[3..].ToArray();
    }

    /// <summary>Starts a download and returns the maximum TransferData block length accepted by the ECU.</summary>
    public async Task<int> RequestDownloadAsync(uint memoryAddress, uint size, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var request = new byte[11];
        request[0] = (byte)UdsService.RequestDownload;
        request[1] = 0x00; // dataFormatIdentifier: no compression, no encryption
        request[2] = 0x44; // addressAndLengthFormatIdentifier: 4-byte address, 4-byte size
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(3), memoryAddress);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(7), size);
        var response = await RequestAsync(request, trace, cancellationToken);
        var lengthBytes = response.Data[0] >> 4;
        var max = 0;
        for (var i = 0; i < lengthBytes; i++)
        {
            max = (max << 8) | response.Data[1 + i];
        }

        return max;
    }

    public Task TransferDataAsync(byte blockSequenceCounter, ReadOnlyMemory<byte> block, UdsTrace? trace, CancellationToken cancellationToken) =>
        RequestAsync([(byte)UdsService.TransferData, blockSequenceCounter, .. block.Span], trace, cancellationToken);

    public Task RequestTransferExitAsync(UdsTrace? trace, CancellationToken cancellationToken) =>
        RequestAsync([(byte)UdsService.RequestTransferExit], trace, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        _lock.Dispose();
    }

    private static bool HasSubFunction(UdsService service) => service is
        UdsService.DiagnosticSessionControl or UdsService.EcuReset or UdsService.TesterPresent or UdsService.ReadDtcInformation;

    private static UdsException Record(UdsException exception, UdsTrace? trace, UdsService service, byte[] request, byte[]? response, Stopwatch stopwatch)
    {
        trace?.Add(new UdsExchange(service, request, response, stopwatch.Elapsed, null));
        return exception;
    }
}
