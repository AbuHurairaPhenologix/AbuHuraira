using System.Buffers.Binary;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Versioning;
using Microsoft.Extensions.Logging;

namespace AutoSphere.EcuSimulation.Ecus;

/// <summary>One software bank of an A/B (dual-bank) flash layout.</summary>
public sealed record SoftwareBank(SoftwareVersion Version, FirmwareBootBehavior BootBehavior, bool IsValid)
{
    public static SoftwareBank Empty { get; } = new(new SoftwareVersion(0, 0, 0), FirmwareBootBehavior.Normal, false);
}

/// <summary>
/// Simulated bootloader with two software banks. New software is always written to the inactive bank,
/// so the running image is never destroyed by a failed update; rollback simply re-activates the
/// previous bank. This mirrors the A/B update schemes used for automotive OTA.
/// </summary>
public abstract partial class EcuSimulator
{
    private const int MaxImageSize = 1024 * 1024;

    private DownloadState? _download;
    private bool _inactiveBankErased;
    private bool _activationPending;
    private bool _rollbackPending;

    public SoftwareBank[] Banks { get; } = new SoftwareBank[2];

    public int ActiveBank { get; private set; }

    /// <summary>True after booting a new image until it is confirmed (routine 0xF002).</summary>
    public bool IsTrialBoot { get; private set; }

    private int InactiveBank => 1 - ActiveBank;

    private void ResetProgrammingState()
    {
        _download = null;
        _inactiveBankErased = false;
    }

    private void ApplyPendingBankSwitch()
    {
        if (_activationPending && Banks[InactiveBank].IsValid)
        {
            Logger.LogInformation("ECU {EcuId}: activating bank {Bank} with software {SoftwareVersion} (trial boot)",
                EcuId, InactiveBank == 0 ? 'A' : 'B', Banks[InactiveBank].Version);
            ActiveBank = InactiveBank;
            IsTrialBoot = true;
        }
        else if (_rollbackPending && Banks[InactiveBank].IsValid)
        {
            Logger.LogWarning("ECU {EcuId}: rolling back to bank {Bank} with software {SoftwareVersion}",
                EcuId, InactiveBank == 0 ? 'A' : 'B', Banks[InactiveBank].Version);
            ActiveBank = InactiveBank;
            IsTrialBoot = false;
        }

        _activationPending = false;
        _rollbackPending = false;
    }

    private bool ProgrammingAllowed(byte sid, out byte[]? negative)
    {
        negative = Session != UdsSession.Programming
            ? UdsProtocol.Negative(sid, NegativeResponseCode.ServiceNotSupportedInActiveSession)
            : !SecurityUnlocked
                ? UdsProtocol.Negative(sid, NegativeResponseCode.SecurityAccessDenied)
                : null;
        return negative is null;
    }

    private byte[] HandleRoutineControl(byte[] request)
    {
        if (request.Length < 4)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
        }

        if ((RoutineControlType)request[1] != RoutineControlType.StartRoutine)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.SubFunctionNotSupported);
        }

        var routineId = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2));
        byte[] Result(byte status) => UdsProtocol.Positive(UdsService.RoutineControl, [request[1], request[2], request[3], status]);

        switch (routineId)
        {
            case RoutineIds.EraseMemory:
                if (!ProgrammingAllowed(request[0], out var eraseDenied))
                {
                    return eraseDenied!;
                }

                Banks[InactiveBank] = SoftwareBank.Empty;
                _inactiveBankErased = true;
                _activationPending = false;
                return Result(0x00);

            case RoutineIds.CheckProgrammingDependencies:
                if (!ProgrammingAllowed(request[0], out var checkDenied))
                {
                    return checkDenied!;
                }

                return Result(ValidateReceivedImage() ? (byte)0x00 : (byte)0x01);

            case RoutineIds.ActivatePreviousBank:
                if (Session == UdsSession.Default)
                {
                    return UdsProtocol.Negative(request[0], NegativeResponseCode.ServiceNotSupportedInActiveSession);
                }

                if (!Banks[InactiveBank].IsValid)
                {
                    return UdsProtocol.Negative(request[0], NegativeResponseCode.ConditionsNotCorrect);
                }

                _rollbackPending = true;
                _activationPending = false;
                return Result(0x00);

            case RoutineIds.ConfirmActiveImage:
                IsTrialBoot = false;
                return Result(0x00);

            default:
                return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestOutOfRange);
        }
    }

    private byte[] HandleRequestDownload(byte[] request)
    {
        if (!ProgrammingAllowed(request[0], out var denied))
        {
            return denied!;
        }

        if (request.Length != 11 || request[2] != 0x44)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
        }

        if (!_inactiveBankErased || _download is not null)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestSequenceError);
        }

        var size = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(7));
        if (size is 0 or > MaxImageSize)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestOutOfRange);
        }

        _download = new DownloadState((int)size);
        var max = UdsProtocol.MaxTransferBlockLength;
        return UdsProtocol.Positive(UdsService.RequestDownload, [0x20, (byte)(max >> 8), (byte)max]);
    }

    private byte[] HandleTransferData(byte[] request)
    {
        if (!ProgrammingAllowed(request[0], out var denied))
        {
            return denied!;
        }

        if (_download is null)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestSequenceError);
        }

        if (request.Length < 3 || request.Length > UdsProtocol.MaxTransferBlockLength)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat);
        }

        var counter = request[1];
        if (counter == _download.LastCounter)
        {
            return UdsProtocol.Positive(UdsService.TransferData, [counter]); // repeated block: acknowledge, do not append
        }

        if (counter != (byte)(_download.LastCounter + 1))
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.WrongBlockSequenceCounter);
        }

        if (_download.Received + request.Length - 2 > _download.Size)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.TransferDataSuspended);
        }

        request.AsSpan(2).CopyTo(_download.Buffer.AsSpan(_download.Received));
        _download.Received += request.Length - 2;
        _download.LastCounter = counter;
        return UdsProtocol.Positive(UdsService.TransferData, [counter]);
    }

    private byte[] HandleRequestTransferExit(byte[] request)
    {
        if (!ProgrammingAllowed(request[0], out var denied))
        {
            return denied!;
        }

        if (_download is null || _download.Received != _download.Size)
        {
            return UdsProtocol.Negative(request[0], NegativeResponseCode.RequestSequenceError);
        }

        _download.Completed = true;
        return UdsProtocol.Positive(UdsService.RequestTransferExit, []);
    }

    /// <summary>
    /// ECU-side plausibility check of the received image (structure and target only). Cryptographic
    /// verification is performed by the gateway before flashing; see docs/architecture/ota-update-flow.md.
    /// </summary>
    private bool ValidateReceivedImage()
    {
        if (_download is not { Completed: true } download
            || !SimulatedFirmwareImage.TryReadHeader(download.Buffer, out var header)
            || header!.EcuType != Type)
        {
            Banks[InactiveBank] = SoftwareBank.Empty;
            _download = null;
            return false;
        }

        Banks[InactiveBank] = new SoftwareBank(header.Version, header.BootBehavior, true);
        _activationPending = true;
        _download = null;
        Logger.LogInformation("ECU {EcuId}: image {Image} written to bank {Bank}", EcuId, SimulatedFirmwareImage.Describe(header), InactiveBank == 0 ? 'A' : 'B');
        return true;
    }

    private sealed class DownloadState(int size)
    {
        public int Size { get; } = size;

        public byte[] Buffer { get; } = new byte[size];

        public int Received { get; set; }

        public byte LastCounter { get; set; }

        public bool Completed { get; set; }
    }
}
