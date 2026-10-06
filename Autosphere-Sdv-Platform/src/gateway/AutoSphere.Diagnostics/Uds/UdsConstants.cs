namespace AutoSphere.Diagnostics.Uds;

/// <summary>UDS service identifiers (byte values as defined in ISO 14229-1). Only the listed subset is supported.</summary>
public enum UdsService : byte
{
    DiagnosticSessionControl = 0x10,
    EcuReset = 0x11,
    ClearDiagnosticInformation = 0x14,
    ReadDtcInformation = 0x19,
    ReadDataByIdentifier = 0x22,
    SecurityAccess = 0x27,
    RoutineControl = 0x31,
    RequestDownload = 0x34,
    TransferData = 0x36,
    RequestTransferExit = 0x37,
    TesterPresent = 0x3E,
}

/// <summary>Negative response codes (byte values as defined in ISO 14229-1).</summary>
public enum NegativeResponseCode : byte
{
    GeneralReject = 0x10,
    ServiceNotSupported = 0x11,
    SubFunctionNotSupported = 0x12,
    IncorrectMessageLengthOrInvalidFormat = 0x13,
    BusyRepeatRequest = 0x21,
    ConditionsNotCorrect = 0x22,
    RequestSequenceError = 0x24,
    RequestOutOfRange = 0x31,
    SecurityAccessDenied = 0x33,
    InvalidKey = 0x35,
    ExceededNumberOfAttempts = 0x36,
    UploadDownloadNotAccepted = 0x70,
    TransferDataSuspended = 0x71,
    GeneralProgrammingFailure = 0x72,
    WrongBlockSequenceCounter = 0x73,
    RequestCorrectlyReceivedResponsePending = 0x78,
    SubFunctionNotSupportedInActiveSession = 0x7E,
    ServiceNotSupportedInActiveSession = 0x7F,
}

public enum UdsSession : byte
{
    Default = 0x01,
    Programming = 0x02,
    Extended = 0x03,
}

public enum UdsResetType : byte
{
    HardReset = 0x01,
    KeyOffOnReset = 0x02,
    SoftReset = 0x03,
}

/// <summary>Sub-functions of ReadDTCInformation (0x19) supported by AutoSphere ECUs.</summary>
public enum ReadDtcSubFunction : byte
{
    ReportNumberOfDtcByStatusMask = 0x01,
    ReportDtcByStatusMask = 0x02,
    ReportDtcSnapshotRecordByDtcNumber = 0x04,
}

/// <summary>DTC status bits (ISO 14229-1 DTCStatusMask).</summary>
[Flags]
public enum DtcStatusBits : byte
{
    None = 0x00,
    TestFailed = 0x01,
    TestFailedThisOperationCycle = 0x02,
    PendingDtc = 0x04,
    ConfirmedDtc = 0x08,
    TestNotCompletedSinceLastClear = 0x10,
    TestFailedSinceLastClear = 0x20,
    TestNotCompletedThisOperationCycle = 0x40,
    WarningIndicatorRequested = 0x80,
}

/// <summary>RoutineControl (0x31) sub-functions.</summary>
public enum RoutineControlType : byte
{
    StartRoutine = 0x01,
    StopRoutine = 0x02,
    RequestRoutineResults = 0x03,
}

/// <summary>Routine identifiers. 0xFF00/0xFF01 follow common OEM practice; 0xF0xx are AutoSphere-specific.</summary>
public static class RoutineIds
{
    public const ushort EraseMemory = 0xFF00;
    public const ushort CheckProgrammingDependencies = 0xFF01;

    /// <summary>Project-specific: make the previous (inactive) software bank active again (rollback).</summary>
    public const ushort ActivatePreviousBank = 0xF001;

    /// <summary>Project-specific: confirm the newly booted image so it is no longer considered "trial".</summary>
    public const ushort ConfirmActiveImage = 0xF002;
}

public static class UdsProtocol
{
    public const byte NegativeResponseSid = 0x7F;
    public const byte PositiveResponseOffset = 0x40;

    /// <summary>Bit 7 of a sub-function byte: suppress the positive response.</summary>
    public const byte SuppressPositiveResponseBit = 0x80;

    /// <summary>SecurityAccess level used for programming (requestSeed = 0x01, sendKey = 0x02).</summary>
    public const byte ProgrammingSecurityLevel = 0x01;

    /// <summary>Maximum TransferData block length announced by the simulated ECUs (incl. SID and counter).</summary>
    public const int MaxTransferBlockLength = 1026;

    /// <summary>Group of all DTCs for ClearDiagnosticInformation.</summary>
    public const uint AllDtcGroups = 0xFFFFFF;

    public static string Describe(NegativeResponseCode code) => code switch
    {
        NegativeResponseCode.ServiceNotSupported => "serviceNotSupported",
        NegativeResponseCode.SubFunctionNotSupported => "subFunctionNotSupported",
        NegativeResponseCode.IncorrectMessageLengthOrInvalidFormat => "incorrectMessageLengthOrInvalidFormat",
        NegativeResponseCode.ConditionsNotCorrect => "conditionsNotCorrect",
        NegativeResponseCode.RequestSequenceError => "requestSequenceError",
        NegativeResponseCode.RequestOutOfRange => "requestOutOfRange",
        NegativeResponseCode.SecurityAccessDenied => "securityAccessDenied",
        NegativeResponseCode.InvalidKey => "invalidKey",
        NegativeResponseCode.ExceededNumberOfAttempts => "exceededNumberOfAttempts",
        NegativeResponseCode.UploadDownloadNotAccepted => "uploadDownloadNotAccepted",
        NegativeResponseCode.GeneralProgrammingFailure => "generalProgrammingFailure",
        NegativeResponseCode.WrongBlockSequenceCounter => "wrongBlockSequenceCounter",
        NegativeResponseCode.RequestCorrectlyReceivedResponsePending => "requestCorrectlyReceived-ResponsePending",
        NegativeResponseCode.ServiceNotSupportedInActiveSession => "serviceNotSupportedInActiveSession",
        NegativeResponseCode.SubFunctionNotSupportedInActiveSession => "subFunctionNotSupportedInActiveSession",
        _ => code.ToString(),
    };

    public static byte[] Positive(UdsService service, ReadOnlySpan<byte> data)
    {
        var response = new byte[1 + data.Length];
        response[0] = (byte)((byte)service + PositiveResponseOffset);
        data.CopyTo(response.AsSpan(1));
        return response;
    }

    public static byte[] Negative(byte serviceId, NegativeResponseCode code) => [NegativeResponseSid, serviceId, (byte)code];
}
