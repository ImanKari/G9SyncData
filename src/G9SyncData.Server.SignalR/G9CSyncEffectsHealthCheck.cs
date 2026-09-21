using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace G9SyncData.Server.SignalR;

/// <summary>
///     Health of the durable effect queue (ADR-0065). An effect is work a COMMITTED push owes - a state transition to apply, a record to
///     derive - and one that has failed for good is that work not being done. The dispatcher logs it, and a log line is easy to miss: a
///     host once lost every offline state change for days this way, with nothing anywhere saying so. This makes it a health signal.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item><b>Unhealthy</b> - at least one effect exhausted its attempts. Fix the handler, then call
///         <see cref="G9CSyncServerEngine.RequeueFailedEffectsAsync" />.</item>
///         <item><b>Degraded</b> - the oldest pending effect is older than the limit: the dispatcher is behind or not running.</item>
///         <item><b>Healthy</b> - otherwise.</item>
///     </list>
///     Register it with <c>services.AddHealthChecks().AddG9SyncEffects()</c>. It lives in this package because the ASP.NET Core shared
///     framework already carries the health-check abstractions; the server core stays free of the dependency.
/// </remarks>
public sealed class G9CSyncEffectsHealthCheck(G9CSyncServerEngine engine, TimeSpan maxPendingAge) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var status = await engine.GetEffectQueueStatusAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["pending"] = status.Pending,
                ["failed"] = status.Failed,
                ["oldestPendingSeconds"] = (long)(status.OldestPendingAge?.TotalSeconds ?? 0)
            };

            if (status.Failed > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"{status.Failed} effect(s) exhausted their attempts: work a committed push owes is not being done. Fix the handler, then requeue them.",
                    data: data);
            }

            if (status.OldestPendingAge is { } age && age > maxPendingAge)
            {
                return HealthCheckResult.Degraded($"The oldest pending effect has waited {age.TotalMinutes:F0} minute(s); the dispatcher is behind or not running.",
                    data: data);
            }

            return HealthCheckResult.Healthy($"{status.Pending} effect(s) pending.", data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The effect queue could not be read.", exception);
        }
    }
}
