using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireRevalidationResult(bool Success, string Code, string Message)
{
    public static SquireRevalidationResult Valid() => new(true, "Valid", "Exact item identity is valid.");
    public static SquireRevalidationResult Fail(string code, string message) => new(false, code, message);
}

public sealed record SquireActionResult(bool Success, string Code, string Message)
{
    public static SquireActionResult Completed() => new(true, "Completed", "Expected slot transition was observed.");
    public static SquireActionResult Fail(string code, string message) => new(false, code, message);
}

public interface ISquireActionGameAdapter
{
    CharacterScope? GetActiveCharacter();
    bool HasConflictingAutomation(SquireDisposition disposition);
    SquireRevalidationResult Revalidate(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition);
    Task<SquireActionResult> ExecuteAsync(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition, CancellationToken cancellationToken);
    void ReleaseOwnedState();
}

public sealed record SquireRunEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string Code,
    string Message,
    EquipmentInstanceFingerprint? Item = null);

public sealed record SquireRunResult(bool Success, string Code, IReadOnlyList<SquireRunEvent> Events);

public sealed class SquireRunner
{
    private readonly ISquireActionGameAdapter adapter;
    private readonly Action<SquireRunEvent> audit;

    public SquireRunner(ISquireActionGameAdapter adapter, Action<SquireRunEvent>? audit = null)
    {
        this.adapter = adapter;
        this.audit = audit ?? (_ => { });
    }

    public async Task<SquireRunResult> RunAsync(SquireActionPlan plan, bool explicitlyConfirmed, CancellationToken cancellationToken)
    {
        var events = new List<SquireRunEvent>();
        void Record(string kind, string code, string message, EquipmentInstanceFingerprint? item = null)
        {
            var value = new SquireRunEvent(DateTimeOffset.UtcNow, kind, code, message, item);
            events.Add(value);
            audit(value);
        }

        if (!explicitlyConfirmed)
            return Stop("ConfirmationRequired", "The reviewed run was not explicitly confirmed.");

        try
        {
            foreach (var action in plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (adapter.GetActiveCharacter() != plan.Character)
                    return Stop("CharacterScopeChanged", "The active character no longer matches the approved plan.", action.Fingerprint);
                if (adapter.HasConflictingAutomation(action.Disposition))
                    return Stop("ConflictingAutomation", "Another automation owns the required game state.", action.Fingerprint);
                if (action.Disposition != plan.Disposition)
                    return Stop("MixedDispositionPlan", "A V1 run may contain only one disposition.", action.Fingerprint);

                var validation = adapter.Revalidate(action.Fingerprint, action.Disposition);
                Record("Revalidation", validation.Code, validation.Message, action.Fingerprint);
                if (!validation.Success)
                    return Stop(validation.Code, validation.Message, action.Fingerprint);

                Record("ActionStart", action.Disposition.ToString(), "Starting validated action.", action.Fingerprint);
                var result = await adapter.ExecuteAsync(action.Fingerprint, action.Disposition, cancellationToken).ConfigureAwait(false);
                Record("ActionResult", result.Code, result.Message, action.Fingerprint);
                if (!result.Success)
                    return Stop(result.Code, result.Message, action.Fingerprint);
            }

            Record("RunComplete", "Completed", "Every approved action completed.");
            return new SquireRunResult(true, "Completed", events);
        }
        catch (OperationCanceledException)
        {
            return Stop("Cancelled", "The run was cancelled.");
        }
        catch (Exception ex)
        {
            return Stop("UnclassifiedFailure", ex.ToString());
        }
        finally
        {
            adapter.ReleaseOwnedState();
        }

        SquireRunResult Stop(string code, string message, EquipmentInstanceFingerprint? item = null)
        {
            Record("RunStopped", code, message, item);
            return new SquireRunResult(false, code, events);
        }
    }
}
