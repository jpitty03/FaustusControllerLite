namespace FaustusControllerLite.Domain;

public static class BankrollAccounting
{
    public static bool TrySettleTerminal(
        BankrollState state,
        string offeredMetadata,
        long originalOfferedAmount,
        long remainingOfferedAmount,
        string wantedMetadata,
        long receivedWantedAmount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (originalOfferedAmount <= 0 || remainingOfferedAmount < 0 ||
            remainingOfferedAmount > originalOfferedAmount || receivedWantedAmount < 0 ||
            offeredMetadata == wantedMetadata ||
            !TryReadReserved(state, offeredMetadata, chaosMetadata, divineMetadata, out var reserved) ||
            reserved != originalOfferedAmount ||
            !CanUseCompletedBucket(state, offeredMetadata, remainingOfferedAmount, chaosMetadata, divineMetadata) ||
            !CanUseCompletedBucket(state, wantedMetadata, receivedWantedAmount, chaosMetadata, divineMetadata))
        {
            return false;
        }

        var shadow = Copy(state);
        AddReserved(shadow, offeredMetadata, -originalOfferedAmount, chaosMetadata, divineMetadata);
        AddCompleted(shadow, offeredMetadata, remainingOfferedAmount, chaosMetadata, divineMetadata);
        AddCompleted(shadow, wantedMetadata, receivedWantedAmount, chaosMetadata, divineMetadata);
        CopyAccounting(shadow, state);
        return true;
    }

    public static bool TryCreditCollected(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0) return false;
        if (metadata == chaosMetadata)
        {
            if (state.CompletedUncollectedChaos < amount) return false;
            var available = checked(state.AvailableChaos + amount);
            state.CompletedUncollectedChaos -= amount;
            state.AvailableChaos = available;
            return true;
        }
        if (metadata == divineMetadata)
        {
            if (state.CompletedUncollectedDivine < amount) return false;
            var available = checked(state.AvailableDivine + amount);
            state.CompletedUncollectedDivine -= amount;
            state.AvailableDivine = available;
            return true;
        }
        if (state.TargetMetadata == metadata && state.CompletedUncollectedTarget >= amount)
        {
            var available = checked(state.AvailableTarget + amount);
            state.CompletedUncollectedTarget -= amount;
            state.AvailableTarget = available;
            return true;
        }
        return false;
    }

    public static bool TryReserve(
        BankrollState state,
        string offeredMetadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0)
        {
            return false;
        }

        if (offeredMetadata == chaosMetadata)
        {
            if (state.AvailableChaos < amount) return false;
            var reserved = checked(state.ReservedChaos + amount);
            state.AvailableChaos -= amount;
            state.ReservedChaos = reserved;
            return true;
        }

        if (offeredMetadata == divineMetadata)
        {
            if (state.AvailableDivine < amount) return false;
            var reserved = checked(state.ReservedDivine + amount);
            state.AvailableDivine -= amount;
            state.ReservedDivine = reserved;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(state.TargetMetadata) && offeredMetadata == state.TargetMetadata)
        {
            if (state.AvailableTarget < amount) return false;
            var reserved = checked(state.ReservedTarget + amount);
            state.AvailableTarget -= amount;
            state.ReservedTarget = reserved;
            return true;
        }

        return false;
    }

    public static bool TryCompleteUncollected(
        BankrollState state,
        string offeredMetadata,
        long offeredAmount,
        string wantedMetadata,
        long wantedAmount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (offeredAmount <= 0 || wantedAmount <= 0 ||
            offeredMetadata != chaosMetadata && offeredMetadata != divineMetadata && offeredMetadata != state.TargetMetadata ||
            wantedMetadata != chaosMetadata && wantedMetadata != divineMetadata &&
                !string.IsNullOrEmpty(state.TargetMetadata) && wantedMetadata != state.TargetMetadata)
        {
            return false;
        }

        var shadow = Copy(state);
        if (offeredMetadata == chaosMetadata)
        {
            if (shadow.ReservedChaos != offeredAmount) return false;
            shadow.ReservedChaos -= offeredAmount;
        }
        else if (offeredMetadata == divineMetadata)
        {
            if (shadow.ReservedDivine != offeredAmount) return false;
            shadow.ReservedDivine -= offeredAmount;
        }
        else
        {
            if (shadow.ReservedTarget != offeredAmount) return false;
            shadow.ReservedTarget -= offeredAmount;
        }

        if (wantedMetadata == chaosMetadata)
        {
            shadow.CompletedUncollectedChaos = checked(shadow.CompletedUncollectedChaos + wantedAmount);
        }
        else if (wantedMetadata == divineMetadata)
        {
            shadow.CompletedUncollectedDivine = checked(shadow.CompletedUncollectedDivine + wantedAmount);
        }
        else
        {
            if (string.IsNullOrEmpty(shadow.TargetMetadata)) shadow.TargetMetadata = wantedMetadata;
            shadow.CompletedUncollectedTarget = checked(shadow.CompletedUncollectedTarget + wantedAmount);
        }

        CopyAccounting(shadow, state);
        return true;
    }

    private static bool TryReadReserved(
        BankrollState state,
        string metadata,
        string chaosMetadata,
        string divineMetadata,
        out long amount)
    {
        if (metadata == chaosMetadata) amount = state.ReservedChaos;
        else if (metadata == divineMetadata) amount = state.ReservedDivine;
        else if (metadata == state.TargetMetadata) amount = state.ReservedTarget;
        else { amount = 0; return false; }
        return true;
    }

    private static bool CanUseCompletedBucket(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata) =>
        amount == 0 || metadata == chaosMetadata || metadata == divineMetadata ||
        string.IsNullOrEmpty(state.TargetMetadata) || state.TargetMetadata == metadata;

    private static void AddReserved(
        BankrollState state,
        string metadata,
        long delta,
        string chaosMetadata,
        string divineMetadata)
    {
        if (metadata == chaosMetadata) state.ReservedChaos = checked(state.ReservedChaos + delta);
        else if (metadata == divineMetadata) state.ReservedDivine = checked(state.ReservedDivine + delta);
        else state.ReservedTarget = checked(state.ReservedTarget + delta);
    }

    private static void AddCompleted(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount == 0) return;
        if (metadata == chaosMetadata) state.CompletedUncollectedChaos = checked(state.CompletedUncollectedChaos + amount);
        else if (metadata == divineMetadata) state.CompletedUncollectedDivine = checked(state.CompletedUncollectedDivine + amount);
        else
        {
            if (string.IsNullOrEmpty(state.TargetMetadata)) state.TargetMetadata = metadata;
            state.CompletedUncollectedTarget = checked(state.CompletedUncollectedTarget + amount);
        }
    }

    private static BankrollState Copy(BankrollState state) => new()
    {
        AvailableChaos = state.AvailableChaos,
        AvailableDivine = state.AvailableDivine,
        ReservedChaos = state.ReservedChaos,
        ReservedDivine = state.ReservedDivine,
        CompletedUncollectedChaos = state.CompletedUncollectedChaos,
        CompletedUncollectedDivine = state.CompletedUncollectedDivine,
        TargetMetadata = state.TargetMetadata,
        AvailableTarget = state.AvailableTarget,
        ReservedTarget = state.ReservedTarget,
        CompletedUncollectedTarget = state.CompletedUncollectedTarget,
    };

    private static void CopyAccounting(BankrollState source, BankrollState destination)
    {
        destination.AvailableChaos = source.AvailableChaos;
        destination.AvailableDivine = source.AvailableDivine;
        destination.ReservedChaos = source.ReservedChaos;
        destination.ReservedDivine = source.ReservedDivine;
        destination.CompletedUncollectedChaos = source.CompletedUncollectedChaos;
        destination.CompletedUncollectedDivine = source.CompletedUncollectedDivine;
        destination.TargetMetadata = source.TargetMetadata;
        destination.AvailableTarget = source.AvailableTarget;
        destination.ReservedTarget = source.ReservedTarget;
        destination.CompletedUncollectedTarget = source.CompletedUncollectedTarget;
    }
}
