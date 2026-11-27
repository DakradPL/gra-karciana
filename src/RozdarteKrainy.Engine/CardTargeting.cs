namespace RozdarteKrainy.Engine;

/// <summary>
/// Ile wskazań (kliknięć) potrzebuje karta i jak zamienić je na pole i cel dla <see cref="Game.PlayCard"/>.
/// Wspólne dla interfejsów (przeglądarka, terminal, później Unity).
/// </summary>
public static class CardTargeting
{
    public static int RequiredArgs(CardDefinition card)
    {
        switch (card.Type)
        {
            case CardType.Unit:
            case CardType.Land:
            case CardType.Artifact:
                return 1;
        }

        var effect = card.OnPlay.FirstOrDefault();
        if (effect is null) return 0;
        return effect.Type switch
        {
            EffectType.SummonTokens => 0,
            EffectType.RallyAllUnits => 0,
            EffectType.DamageEnemyFortress => 0,
            EffectType.RelocateEnemyUnitAndDraw => 2,
            _ => 1,
        };
    }

    public static (Position? Placement, Target? Target) Map(CardDefinition card, IReadOnlyList<Target> args, int caster)
    {
        Position? first = args.Count > 0 ? args[0].Position : null;
        Position? second = args.Count > 1 ? args[1].Position : null;
        Target? firstTarget = args.Count > 0 ? args[0] : null;
        Target? secondTarget = args.Count > 1 ? args[1] : null;

        switch (card.Type)
        {
            case CardType.Unit:
            {
                var heals = card.OnPlay.Any(e => e.Type == EffectType.HealUnitOrOwnFortress);
                if (secondTarget is null && heals) secondTarget = Target.Fortress(caster);
                return (first, secondTarget);
            }
            case CardType.Land:
                return (first, null);
            case CardType.Artifact:
                return (null, firstTarget);
        }

        var effect = card.OnPlay.FirstOrDefault();
        if (effect is null) return (null, null);
        if (effect.Type == EffectType.DamageEnemiesInLane) return (first, null);
        if (effect.Type == EffectType.RelocateEnemyUnitAndDraw) return (second, firstTarget);
        return (null, firstTarget);
    }
}
