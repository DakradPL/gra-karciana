namespace RozdarteKrainy.Engine;

/// <summary>
/// Prosty, zachłanny bot. Próbuje zagrać najdroższe karty, na które go stać, w sensownych miejscach,
/// potem używa mocy Bohatera i kończy turę. Wszystkie ruchy sprawdza silnik, więc bot nie oszukuje.
/// </summary>
public static class SimpleBot
{
    private const int MaxActionsPerTurn = 30;

    public static void PlayTurn(Game game)
    {
        for (var i = 0; i < MaxActionsPerTurn && !game.State.IsOver; i++)
        {
            if (!TryBestAction(game)) break;
        }
        if (!game.State.IsOver) game.EndTurn();
    }

    private static bool TryBestAction(Game game)
    {
        var s = game.State;
        var me = s.Active;

        var order = me.Hand.Select((card, index) => (card, index))
                           .Where(x => x.card.Cost <= me.Essence)
                           .OrderByDescending(x => x.card.Cost)
                           .ToList();
        foreach (var (card, index) in order)
        {
            foreach (var (placement, target) in Candidates(s, card))
            {
                if (game.PlayCard(index, placement, target).Ok) return true;
            }
        }

        if (!me.HeroPowerUsed && me.Essence >= Rules.HeroPowerCost)
        {
            foreach (var target in HeroTargets(s))
            {
                if (game.UseHeroPower(target).Ok) return true;
            }
        }

        if (me.HasSpark && me.Hand.Any(c => c.Cost == me.Essence + 1))
            return game.UseSpark().Ok;

        return false;
    }

    private static IEnumerable<(Position? Placement, Target? Target)> Candidates(GameState s, CardDefinition card)
    {
        var me = s.Active;
        var foe = s.Opponent;

        switch (card.Type)
        {
            case CardType.Unit:
            {
                var heal = card.OnPlay.Any(e => e.Type == EffectType.HealUnitOrOwnFortress);
                Target? extra = heal ? Target.Fortress(me.Index) : null;
                foreach (var pos in UnitCells(s, card))
                    yield return (pos, extra);
                yield break;
            }
            case CardType.Artifact:
                foreach (var (pos, _) in me.Units().OrderByDescending(x => x.Unit.Attack))
                    yield return (null, new Target(me.Index, pos));
                yield break;
            case CardType.Land:
            {
                var lanes = Enumerable.Range(0, Rules.Lanes);
                int Count(PlayerState p, int lane) => p.Units().Count(x => x.Pos.Lane == lane);
                var best = card.Land == LandType.Scorch
                    ? lanes.OrderByDescending(l => Count(foe, l) - Count(me, l)).First()
                    : lanes.OrderByDescending(l => Count(me, l) - Count(foe, l)).First();
                var gain = card.Land == LandType.Scorch ? Count(foe, best) - Count(me, best) : Count(me, best) - Count(foe, best);
                if (gain > 0 && s.Lands[best]?.Id != card.Id) yield return (new Position(best, Row.Front), null);
                yield break;
            }
        }

        // Zaklęcia: decyduje pierwszy efekt.
        var effect = card.OnPlay.FirstOrDefault();
        if (effect is null) yield break;

        switch (effect.Type)
        {
            case EffectType.DamageUnit:
                foreach (var (pos, unit) in foe.Units()
                             .OrderByDescending(x => s.RemainingHealth(x.Unit, x.Pos.Lane) <= effect.Amount)
                             .ThenByDescending(x => x.Unit.Attack))
                    yield return (null, new Target(foe.Index, pos));
                break;
            case EffectType.DamageEnemiesInLane:
                foreach (var lane in Enumerable.Range(0, Rules.Lanes)
                             .Where(l => foe.HasUnitInLane(l))
                             .OrderByDescending(l => foe.Units().Count(x => x.Pos.Lane == l)))
                    yield return (new Position(lane, Row.Front), null);
                break;
            case EffectType.BuffUnit:
                foreach (var (pos, _) in me.Units().OrderByDescending(x => x.Unit.Attack))
                    yield return (null, new Target(me.Index, pos));
                break;
            case EffectType.RootUnit:
                foreach (var (pos, _) in foe.Units().Where(x => x.Unit.Attack > 0).OrderByDescending(x => x.Unit.Attack))
                    yield return (null, new Target(foe.Index, pos));
                break;
            case EffectType.RelocateEnemyUnitAndDraw:
                foreach (var (pos, _) in foe.Units().Where(x => x.Pos.Row == Row.Front).OrderByDescending(x => x.Unit.Attack))
                    foreach (var dest in foe.EmptyCells().Where(c => c.Lane != pos.Lane))
                        yield return (dest, new Target(foe.Index, pos));
                break;
            case EffectType.SummonTokens:
                if (me.EmptyCells().Any()) yield return (null, null);
                break;
            case EffectType.RallyAllUnits:
                if (me.Units().Count(x => !x.Unit.SummoningSick) >= 2) yield return (null, null);
                break;
            case EffectType.HealUnitOrOwnFortress:
                if (me.Fortress <= Rules.FortressHealth - effect.Amount) yield return (null, Target.Fortress(me.Index));
                break;
            default:
                yield return (null, null);
                break;
        }
    }

    /// <summary>Pola dla jednostki: najpierw ścieżki, na których można przejąć Kamień.</summary>
    private static IEnumerable<Position> UnitCells(GameState s, CardDefinition card)
    {
        var me = s.Active;
        var foe = s.Opponent;
        var preferBack = card.Keywords.Contains(Keyword.Ranged) || card.Attack == 0;
        var first = preferBack ? Row.Back : Row.Front;
        var second = preferBack ? Row.Front : Row.Back;

        var lanes = Enumerable.Range(0, Rules.Lanes)
            .OrderByDescending(l => foe.Board[l, (int)Row.Front] is null && s.Stones[l] != me.Index)
            .ThenByDescending(l => s.Stones[l] == foe.Index)
            .ToList();

        foreach (var lane in lanes)
            if (me.Board[lane, (int)first] is null) yield return new Position(lane, first);
        foreach (var lane in lanes)
            if (me.Board[lane, (int)second] is null) yield return new Position(lane, second);
    }

    private static IEnumerable<Target> HeroTargets(GameState s)
    {
        var me = s.Active;
        var foe = s.Opponent;
        switch (me.Hero.HeroPower)
        {
            case HeroPowerType.FlameStrike:
            {
                var amount = foe.Fortress <= Rules.HeroAwakenFortressThreshold ? 2 : 1;
                foreach (var (pos, _) in foe.Units()
                             .Where(x => s.RemainingHealth(x.Unit, x.Pos.Lane) <= amount)
                             .OrderByDescending(x => x.Unit.Attack))
                    yield return new Target(foe.Index, pos);
                yield return Target.Fortress(foe.Index);
                break;
            }
            case HeroPowerType.ForestBlessing:
                foreach (var (pos, _) in me.Units().OrderByDescending(x => x.Unit.Attack))
                    yield return new Target(me.Index, pos);
                break;
        }
    }
}
