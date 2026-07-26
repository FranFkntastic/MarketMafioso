using System;
using System.Numerics;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record PositionFrameOneShotView(
    bool Prepared,
    bool CanPrepare,
    bool CanFire,
    string State,
    string Message,
    string Readiness,
    string? BellGameObjectId,
    float? Distance,
    DateTimeOffset? ExpiresAtUtc);

internal sealed partial class RemoteSummoningBellProbe
{
    private static readonly TimeSpan PositionFramePreparationLifetime = TimeSpan.FromSeconds(60);

    private readonly PositionFrameOneShotPreparationSlot positionFrameOneShotSlot = new();

    public PositionFrameOneShotView GetPositionFrameOneShotView()
    {
        var observation = bell.ObserveLoadedBell();
        var prepared = positionFrameOneShotSlot.Peek();
        var canPrepare = CanPreparePositionFrameOneShot(observation, out var prepareReadiness);
        var fireReadiness = "No position-frame one-shot is prepared.";
        var canFire = prepared is not null &&
            ValidatePositionFramePreparation(prepared, out fireReadiness);
        var readiness = prepared is null
            ? prepareReadiness
            : fireReadiness;

        return new(
            prepared is not null,
            canPrepare,
            canFire,
            prepared is null
                ? "Unprepared"
                : canFire
                    ? "Prepared"
                    : "Prepared but invalid",
            prepared is null
                ? "No position-frame transmission is prepared."
                : canFire
                    ? "The exact bell, territory, truthful position, and hypothetical position are frozen for one reviewed transmission."
                    : "The prepared one-shot can no longer fire and must be prepared again.",
            readiness,
            FormatGameObjectId(prepared?.BellGameObjectId ?? observation.BellGameObjectId),
            observation.Available ? observation.Distance : null,
            prepared?.ExpiresAtUtc);
    }

    public string PreparePositionFrameOneShot()
    {
        if (disposed)
            return "The position-frame one-shot is unavailable because the probe has been disposed.";

        var observation = bell.ObserveLoadedBell();
        if (!CanPreparePositionFrameOneShot(observation, out var readiness))
            return readiness;

        var playerPosition = objectTable.LocalPlayer!.Position;
        if (!TryFindGameObjectPosition(observation.BellGameObjectId, out var bellPosition))
            return "The loaded bell position is unavailable.";
        if (!TryComputeBellRadiusPosition(
                playerPosition,
                bellPosition,
                0.5f,
                out var bellAdjacentPosition))
        {
            return "A bell-adjacent hypothetical position could not be derived.";
        }

        var now = DateTimeOffset.UtcNow;
        positionFrameOneShotSlot.Store(new(
            Guid.NewGuid(),
            now,
            now + PositionFramePreparationLifetime,
            clientState.TerritoryType,
            observation.BellGameObjectId,
            observation.Distance,
            observation.OrdinaryInteractionDistance,
            playerPosition,
            bellPosition,
            bellAdjacentPosition));
        return
            $"Prepared one exact position-frame transmission for bell {observation.BellGameObjectId:X}. " +
            "Nothing was sent; use the separately reviewed transmit control within 60 seconds.";
    }

    public string CancelPositionFrameOneShot()
    {
        if (!positionFrameOneShotSlot.Cancel())
            return "No position-frame one-shot is prepared.";

        return "Cancelled the prepared position-frame one-shot without sending anything.";
    }

    public string TransmitPreparedPositionFrameOneShot()
    {
        var prepared = positionFrameOneShotSlot.Consume();
        if (prepared is null)
            return "No position-frame one-shot is prepared.";
        if (!ValidatePositionFramePreparation(prepared, out var readiness))
            return $"The prepared one-shot was consumed without transmission: {readiness}";

        if (!AutoRetainerSuppressionLease.TryAcquire(
                autoRetainer,
                out var suppression,
                out var suppressionMessage))
        {
            return suppressionMessage;
        }
        autoRetainerSuppression = suppression;

        var startedAtUtc = DateTimeOffset.UtcNow;
        var territoryId = clientState.TerritoryType;
        var startPosition = CapturePosition();
        var submission = bell.TryOpenLoadedWithPositionFrameSubstitution(
            prepared.TruthfulPosition,
            prepared.BellAdjacentPosition);
        if (!submission.Submitted)
        {
            ReleaseAutoRetainerSuppression();
            var evidence = CreateEvidence(
                startedAtUtc,
                DateTimeOffset.UtcNow,
                territoryId,
                startPosition,
                CapturePosition(),
                submission,
                PositionFrameOneShotProbePhase,
                "NotSubmitted",
                false,
                false);
            var path = WriteEvidence(evidence);
            view = new(
                false,
                false,
                "Not submitted",
                submission.Message,
                submission.Message,
                FormatGameObjectId(submission.BellGameObjectId),
                submission.BellGameObjectId == 0 ? null : submission.Distance,
                submission.BellGameObjectId == 0 ? null : submission.OrdinaryInteractionDistance,
                path);
            return submission.Message;
        }

        session = new(
            startedAtUtc,
            startedAtUtc + ObservationWindow,
            territoryId,
            startPosition,
            submission,
            PositionFrameOneShotProbePhase);
        view = new(
            true,
            false,
            "Observing exact one-shot",
            "Invoked one stock out-of-range bell interaction and armed exactly one fail-closed compact position-frame substitution.",
            $"No retry can occur. {suppressionMessage}",
            FormatGameObjectId(submission.BellGameObjectId),
            submission.Distance,
            submission.OrdinaryInteractionDistance,
            view.LastEvidencePath);
        log.Warning(
            "[MarketMafioso] Transmitted-control session started for bell {BellGameObjectId:X} at truthful distance {Distance:F3}; preparation {PreparationId}; no retry is possible.",
            submission.BellGameObjectId,
            submission.Distance,
            prepared.Id);
        return submission.Message;
    }

    private bool CanPreparePositionFrameOneShot(
        RemoteSummoningBellObservation observation,
        out string readiness)
    {
        if (!configuration.EnableMarketDiagnostics)
        {
            readiness = "Enable Market Diagnostics before preparing the one-shot.";
            return false;
        }
        if (!clientState.IsLoggedIn)
        {
            readiness = "A logged-in character is required.";
            return false;
        }
        if (!observation.Available || !observation.OutsideOrdinaryInteractionRange)
        {
            readiness = observation.Message;
            return false;
        }
        if (session is not null ||
            normalCaptureSession is not null ||
            yieldProbeSession is not null ||
            warmSessionProbeSession is not null ||
            retainerRpcProbeSession is not null)
        {
            readiness = "Another remote-bell diagnostic is active.";
            return false;
        }
        if (IsRetainerListReady())
        {
            readiness = "Close the existing RetainerList before preparing the one-shot.";
            return false;
        }

        readiness =
            $"Ready to freeze bell {observation.BellGameObjectId:X} at truthful distance {observation.Distance:F3} for 60 seconds. Preparation sends nothing.";
        return true;
    }

    private bool ValidatePositionFramePreparation(
        PositionFrameOneShotPreparation prepared,
        out string readiness)
    {
        if (DateTimeOffset.UtcNow > prepared.ExpiresAtUtc)
        {
            readiness = "The 60-second preparation expired.";
            return false;
        }
        if (!configuration.EnableMarketDiagnostics || !clientState.IsLoggedIn)
        {
            readiness = "Market Diagnostics or the logged-in session is no longer available.";
            return false;
        }
        if (clientState.TerritoryType != prepared.TerritoryId)
        {
            readiness = "The territory changed after preparation.";
            return false;
        }
        if (session is not null ||
            normalCaptureSession is not null ||
            yieldProbeSession is not null ||
            warmSessionProbeSession is not null ||
            retainerRpcProbeSession is not null ||
            IsRetainerListReady())
        {
            readiness = "Another bell diagnostic or retainer session became active.";
            return false;
        }

        var observation = bell.ObserveLoadedBell();
        if (!observation.Available ||
            !observation.OutsideOrdinaryInteractionRange ||
            observation.BellGameObjectId != prepared.BellGameObjectId)
        {
            readiness = "The exact prepared bell is no longer loaded outside ordinary range.";
            return false;
        }
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition is null ||
            Vector3.Distance(playerPosition.Value, prepared.TruthfulPosition) >
            PositionFrameShadowAnalyzer.PositionTolerance)
        {
            readiness = "The truthful player position changed after preparation.";
            return false;
        }
        if (!TryFindGameObjectPosition(prepared.BellGameObjectId, out var bellPosition) ||
            Vector3.Distance(bellPosition, prepared.BellPosition) >
            PositionFrameShadowAnalyzer.PositionTolerance)
        {
            readiness = "The bell position changed after preparation.";
            return false;
        }
        if (!TryComputeBellRadiusPosition(
                playerPosition.Value,
                bellPosition,
                0.5f,
                out var derivedHypothetical) ||
            Vector3.Distance(derivedHypothetical, prepared.BellAdjacentPosition) >
            PositionFrameShadowAnalyzer.PositionTolerance)
        {
            readiness = "The prepared bell-adjacent position no longer matches current geometry.";
            return false;
        }

        readiness =
            $"Prepared for bell {prepared.BellGameObjectId:X}: truthful {Format(prepared.TruthfulPosition)} -> hypothetical {Format(prepared.BellAdjacentPosition)}. Transmit consumes this preparation whether it succeeds or aborts.";
        return true;
    }

    private static string Format(Vector3 value) =>
        $"({value.X:F3}, {value.Y:F3}, {value.Z:F3})";

}

internal sealed record PositionFrameOneShotPreparation(
    Guid Id,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    uint TerritoryId,
    ulong BellGameObjectId,
    float TruthfulDistance,
    float OrdinaryInteractionDistance,
    Vector3 TruthfulPosition,
    Vector3 BellPosition,
    Vector3 BellAdjacentPosition);

internal sealed class PositionFrameOneShotPreparationSlot
{
    private readonly object gate = new();
    private PositionFrameOneShotPreparation? current;

    public void Store(PositionFrameOneShotPreparation preparation)
    {
        lock (gate)
            current = preparation;
    }

    public PositionFrameOneShotPreparation? Peek()
    {
        lock (gate)
            return current;
    }

    public PositionFrameOneShotPreparation? Consume()
    {
        lock (gate)
        {
            var consumed = current;
            current = null;
            return consumed;
        }
    }

    public bool Cancel()
    {
        lock (gate)
        {
            var hadPreparation = current is not null;
            current = null;
            return hadPreparation;
        }
    }
}
