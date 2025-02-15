namespace RebornRotations.Ranged;

[Rotation("FRU", CombatType.PvE, GameVersion = "7.15")]
[SourceCode(Path = "main/CustomRotations/Ranged/MCH_FRU.cs")]
[Api(4)]
public sealed class MCH_FRU : MachinistRotation
{
    #region Config Options
    [RotationConfig(CombatType.PvE, Name = "Adjust Burst Timing for FRU")]
    private bool AdjustBurstTiming { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Hold Drill/Air Anchor for Burst")]
    private bool HoldBigHitsForBuffs { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Save Tactician for Critical Raid Damage")]
    private bool SaveTactician { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Bioblaster while moving")]
    private bool BioMove { get; set; } = true;
    #endregion

    #region Countdown logic
    protected override IAction? CountDownAction(float remainTime)
    {
        if (remainTime < 5 && ReassemblePvE.CanUse(out var act)) return act;
        if (IsBurst && remainTime <= 1f && UseBurstMedicine(out act)) return act;
        return base.CountDownAction(remainTime);
    }
    #endregion

    #region oGCD Logic
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (IsBurst && AdjustBurstTiming && TimeForBurstMeds(out act, nextGCD)) return true;
        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (SaveTactician && HighRaidDamagePhase() && TacticianPvE.CanUse(out act)) return true;
        return base.DefenseAreaAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        if (IsBurst && HoldBigHitsForBuffs && ShouldDelayBigHits(nextGCD)) return false;
        return base.AttackAbility(nextGCD, out act);
    }
    #endregion

    #region GCD Logic
    protected override bool GeneralGCD(out IAction? act)
    {
        if ((BioMove || (!IsMoving && !BioMove)) && BioblasterPvE.CanUse(out act, usedUp: true)) return true;

        if (HoldBigHitsForBuffs && ShouldDelayBigHits(null)) return false;

        if (DrillPvE.CanUse(out act, usedUp: true)) return true;
        if (AirAnchorPvE.CanUse(out act)) return true;
        if (ChainSawPvE.CanUse(out act)) return true;
        if (SpreadShotPvE.CanUse(out act)) return true;
        if (CleanShotPvE.CanUse(out act)) return true;

        return base.GeneralGCD(out act);
    }
    #endregion

    #region Extra Methods
    private bool HighRaidDamagePhase()
    {
        return EncounterManager.CurrentBoss != null && EncounterManager.CurrentBoss.Phase == "Enrage";
    }

    private bool ShouldDelayBigHits(IAction? nextGCD)
    {
        return !Player.HasStatus(true, StatusID.Reassembled) &&
               (nextGCD?.IsTheSameTo(true, DrillPvE, AirAnchorPvE) ?? true) &&
               BuffWindowsSoon();
    }

    private bool BuffWindowsSoon()
    {
        return Party.HasBuff(PartyBuffID.ChainStratagem, 5) ||
               Party.HasBuff(PartyBuffID.BattleLitany, 5) ||
               Party.HasBuff(PartyBuffID.TechStep, 5);
    }

    private bool TimeForBurstMeds(out IAction? act, IAction nextGCD)
    {
        if (AdjustBurstTiming && BuffWindowsSoon() && WildfirePvE.Cooldown.WillHaveOneChargeGCD(1))
            return UseBurstMedicine(out act);
        act = null;
        return false;
    }
    #endregion
}
