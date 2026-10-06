using AutoSphere.SharedKernel.Ota;

namespace AutoSphere.Domain.Ota;

/// <summary>Allowed transitions of an OTA deployment (see <see cref="OtaUpdateStatus"/> for the diagram).</summary>
public static class OtaStateMachine
{
    private static readonly Dictionary<OtaUpdateStatus, OtaUpdateStatus[]> Transitions = new()
    {
        [OtaUpdateStatus.Created] = [OtaUpdateStatus.Pending, OtaUpdateStatus.Cancelled, OtaUpdateStatus.Failed],
        [OtaUpdateStatus.Pending] = [OtaUpdateStatus.Downloading, OtaUpdateStatus.Failed, OtaUpdateStatus.Cancelled],
        [OtaUpdateStatus.Downloading] = [OtaUpdateStatus.Verifying, OtaUpdateStatus.Failed],
        [OtaUpdateStatus.Verifying] = [OtaUpdateStatus.Installing, OtaUpdateStatus.Failed],
        [OtaUpdateStatus.Installing] = [OtaUpdateStatus.Restarting, OtaUpdateStatus.Failed],
        [OtaUpdateStatus.Restarting] = [OtaUpdateStatus.HealthChecking, OtaUpdateStatus.RollingBack, OtaUpdateStatus.RollbackFailed],
        [OtaUpdateStatus.HealthChecking] = [OtaUpdateStatus.Succeeded, OtaUpdateStatus.RollingBack, OtaUpdateStatus.RollbackFailed],
        [OtaUpdateStatus.RollingBack] = [OtaUpdateStatus.RolledBack, OtaUpdateStatus.RollbackFailed],
    };

    public static bool CanTransition(OtaUpdateStatus from, OtaUpdateStatus to) =>
        from == to ? !from.IsTerminal() : Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// The vehicle reports only the states it executes; intermediate states may be skipped when status
    /// messages are coalesced. A report is accepted when the target is reachable from the current state.
    /// </summary>
    public static bool IsReachable(OtaUpdateStatus from, OtaUpdateStatus to)
    {
        if (CanTransition(from, to))
        {
            return true;
        }

        var visited = new HashSet<OtaUpdateStatus>();
        var queue = new Queue<OtaUpdateStatus>([from]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!Transitions.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var next in targets.Where(visited.Add))
            {
                if (next == to)
                {
                    return true;
                }

                queue.Enqueue(next);
            }
        }

        return false;
    }
}
