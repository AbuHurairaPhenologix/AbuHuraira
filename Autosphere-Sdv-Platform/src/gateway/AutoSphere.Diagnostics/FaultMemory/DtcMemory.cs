using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Diagnostics.FaultMemory;

public enum DtcClearResult
{
    Cleared,
    NotStored,
    ConditionStillPresent,
}

/// <summary>
/// Non-volatile fault memory of a simulated ECU with UDS-style status bits and a simple counter-based
/// debounce: a fault must be reported as failed in <see cref="ConfirmationThreshold"/> consecutive monitor
/// cycles before it becomes <c>confirmedDTC</c> and its snapshot (freeze frame) is captured.
/// </summary>
public sealed class DtcMemory
{
    private readonly Dictionary<DtcCode, Entry> _entries = [];
    private readonly object _gate = new();

    public DtcMemory(int confirmationThreshold = 5) => ConfirmationThreshold = confirmationThreshold;

    public int ConfirmationThreshold { get; }

    /// <summary>Raised whenever the stored status of any DTC changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Reports the result of one monitor cycle for <paramref name="code"/>.</summary>
    public void Report(DtcCode code, bool failed, Func<IReadOnlyList<(DataIdentifierDefinition Definition, byte[] Data)>> captureSnapshot)
    {
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        var changed = false;
        lock (_gate)
        {
            _entries.TryGetValue(code, out var entry);
            if (failed)
            {
                entry ??= new Entry();
                var before = entry.Status;
                entry.Status |= DtcStatusBits.TestFailed | DtcStatusBits.TestFailedThisOperationCycle
                                | DtcStatusBits.PendingDtc | DtcStatusBits.TestFailedSinceLastClear;
                entry.Debounce++;
                if (entry.Debounce >= ConfirmationThreshold && !entry.Status.HasFlag(DtcStatusBits.ConfirmedDtc))
                {
                    entry.Status |= DtcStatusBits.ConfirmedDtc;
                    entry.Snapshot = captureSnapshot();
                }

                _entries[code] = entry;
                changed = before != entry.Status;
            }
            else if (entry is not null)
            {
                entry.Debounce = 0;
                if (!entry.Status.HasFlag(DtcStatusBits.ConfirmedDtc))
                {
                    // A pending fault that healed before confirmation is not kept.
                    _entries.Remove(code);
                    changed = true;
                }
                else if (entry.Status.HasFlag(DtcStatusBits.TestFailed))
                {
                    entry.Status &= ~DtcStatusBits.TestFailed;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Returns DTCs whose status has at least one bit in common with <paramref name="mask"/>.</summary>
    public IReadOnlyList<UdsDtcRecord> Query(DtcStatusBits mask)
    {
        lock (_gate)
        {
            return _entries.Where(e => (e.Value.Status & mask) != 0)
                .Select(e => new UdsDtcRecord(e.Key, e.Value.Status))
                .OrderBy(r => r.Code.Value, StringComparer.Ordinal)
                .ToList();
        }
    }

    public bool TryGetSnapshot(DtcCode code, out DtcStatusBits status, out IReadOnlyList<(DataIdentifierDefinition Definition, byte[] Data)> snapshot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(code, out var entry))
            {
                status = entry.Status;
                snapshot = entry.Snapshot;
                return true;
            }
        }

        status = DtcStatusBits.None;
        snapshot = [];
        return false;
    }

    public bool IsTestFailed(DtcCode code)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(code, out var entry) && entry.Status.HasFlag(DtcStatusBits.TestFailed);
        }
    }

    /// <summary>
    /// Clears one DTC or (code = null) every DTC whose fault condition is no longer present.
    /// </summary>
    /// <remarks>
    /// Project policy, deliberately stricter than typical ECUs: a DTC whose test is <i>currently failing</i>
    /// cannot be cleared (NRC 0x22 conditionsNotCorrect). Real ECUs usually clear it and set it again
    /// in the next monitor cycle; refusing makes the "clear eligible DTCs" rule observable.
    /// </remarks>
    public DtcClearResult Clear(DtcCode? code)
    {
        var result = DtcClearResult.NotStored;
        lock (_gate)
        {
            if (code is { } single)
            {
                if (_entries.TryGetValue(single, out var entry))
                {
                    if (entry.Status.HasFlag(DtcStatusBits.TestFailed))
                    {
                        return DtcClearResult.ConditionStillPresent;
                    }

                    _entries.Remove(single);
                    result = DtcClearResult.Cleared;
                }
            }
            else
            {
                foreach (var key in _entries.Where(e => !e.Value.Status.HasFlag(DtcStatusBits.TestFailed)).Select(e => e.Key).ToList())
                {
                    _entries.Remove(key);
                    result = DtcClearResult.Cleared;
                }
            }
        }

        if (result == DtcClearResult.Cleared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    private sealed class Entry
    {
        public DtcStatusBits Status { get; set; }

        public int Debounce { get; set; }

        public IReadOnlyList<(DataIdentifierDefinition Definition, byte[] Data)> Snapshot { get; set; } = [];
    }
}
