namespace RozdarteKrainy.Engine;

/// <summary>Stałe zasad z dokumentu projektowego.</summary>
public static class Rules
{
    public const int FortressHealth = 25;
    public const int MaxBaseEssence = 7;
    public const int StartingHand = 4;
    public const int HandLimit = 10;
    public const int TurnLimit = 20;
    public const int HeroPowerCost = 2;
    public const int MoveCost = 1;
    public const int Lanes = 3;
    public const int DeckSize = 30;
    public const int MaxCopies = 2;
    public const int MaxLegendaryCopies = 1;
    public const int MaxSecondaryFactionCards = 10;
    public const int MaxLands = 4;
    public const int HeroAwakenFortressThreshold = 12;
    public const int HeroAwakenStones = 2;
}

public enum Faction { Neutral, FlameOrder, ForestCircle }

public enum CardType { Hero, Unit, Spell, Artifact, Land }

public enum Rarity { Common, Rare, Epic, Legendary }

public enum Row { Front = 0, Back = 1 }

public enum Keyword { Charge, Guard, Ranged, Stealth, Pierce, StoneWarden }

public enum EffectType
{
    /// <summary>Obrażenia wybranej jednostce.</summary>
    DamageUnit,
    /// <summary>Obrażenia wrogiej Twierdzy (z opcjonalną Więzią).</summary>
    DamageEnemyFortress,
    /// <summary>Leczenie własnej jednostki albo własnej Twierdzy.</summary>
    HealUnitOrOwnFortress,
    /// <summary>Trwałe wzmocnienie własnej jednostki (też Artefakty).</summary>
    BuffUnit,
    /// <summary>Wzmocnienie jednostki, która właśnie weszła, gdy spełniona jest Więź.</summary>
    BuffSelfIfBond,
    /// <summary>Obrażenia każdej wrogiej jednostce na wybranej ścieżce.</summary>
    DamageEnemiesInLane,
    /// <summary>Przywołanie żetonów na wolne pola.</summary>
    SummonTokens,
    /// <summary>+atak do końca tury i darmowy ruch dla wszystkich twoich jednostek.</summary>
    RallyAllUnits,
    /// <summary>Wroga jednostka nie atakuje i nie rusza się w następnej turze rywala.</summary>
    RootUnit,
    /// <summary>Przesunięcie wrogiej jednostki na inną ścieżkę po jej stronie i dobranie karty.</summary>
    RelocateEnemyUnitAndDraw,
}

public enum TriggerType { None, DawnSummonTokenInLane, DuskGrowIfStoneHeld }

public enum LandType { None, Scorch, Grove }

public enum HeroPowerType { FlameStrike, ForestBlessing }

public enum WinReason { Demolition, Dominance, TurnLimit }

public enum GamePhase { Mulligan, Actions, Over }

public readonly record struct Position(int Lane, Row Row)
{
    public bool IsValid => Lane >= 0 && Lane < Rules.Lanes;
    public override string ToString() => $"{Lane}:{Row}";
}

/// <summary>Cel efektu: jednostka na polu gracza albo Twierdza gracza (Position == null).</summary>
public readonly record struct Target(int Player, Position? Position)
{
    public static Target UnitAt(int player, int lane, Row row) => new(player, new Position(lane, row));
    public static Target Fortress(int player) => new(player, null);
    public bool IsFortress => Position is null;
}

public sealed record ActionResult(bool Ok, string? Error)
{
    public static readonly ActionResult Success = new(true, null);
    public static ActionResult Fail(string error) => new(false, error);
}

public sealed class EffectDefinition
{
    public EffectType Type { get; init; }
    public int Amount { get; init; }
    public int Attack { get; init; }
    public int Health { get; init; }
    public int? BondAmount { get; init; }
    public string? TokenId { get; init; }
    public List<Keyword> GrantKeywords { get; init; } = new();
}

public sealed class CardDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Faction Faction { get; init; }
    public CardType Type { get; init; }
    public Rarity Rarity { get; init; }
    public int Cost { get; init; }
    public int Attack { get; init; }
    public int Health { get; init; }
    public List<Keyword> Keywords { get; init; } = new();
    public int? EchoCost { get; init; }
    public bool IsToken { get; init; }
    public List<EffectDefinition> OnPlay { get; init; } = new();
    public TriggerType Trigger { get; init; }
    public string? TokenId { get; init; }
    public LandType Land { get; init; }
    public HeroPowerType? HeroPower { get; init; }
    public string Text { get; init; } = "";

    public override string ToString() => Name;
}

public sealed class Unit
{
    public int Uid { get; init; }
    public CardDefinition Card { get; init; } = null!;
    public int Owner { get; init; }
    public int BaseAttack { get; set; }
    public int BaseHealth { get; set; }
    public int Damage { get; set; }
    public int TempAttack { get; set; }
    public HashSet<Keyword> Keywords { get; } = new();
    /// <summary>Zagrana w tej turze bez Szarży: nie atakuje.</summary>
    public bool SummoningSick { get; set; }
    public bool MovedThisTurn { get; set; }
    public bool FreeMove { get; set; }
    public bool Rooted { get; set; }
    /// <summary>Zagrana przez Echo: po śmierci znika z gry zamiast trafić do Otchłani.</summary>
    public bool Exiled { get; set; }
    public List<CardDefinition> Attachments { get; } = new();

    public int Attack => Math.Max(0, BaseAttack + TempAttack);
    public bool Has(Keyword keyword) => Keywords.Contains(keyword);
    public override string ToString() => $"{Card.Name} {Attack}/{BaseHealth - Damage}";
}

public sealed class PlayerState
{
    public int Index { get; init; }
    public CardDefinition Hero { get; set; } = null!;
    public int Fortress { get; set; } = Rules.FortressHealth;
    /// <summary>Wierzch talii to indeks 0.</summary>
    public List<CardDefinition> Deck { get; } = new();
    public List<CardDefinition> Hand { get; } = new();
    /// <summary>Otchłań: karty zużyte i zniszczone.</summary>
    public List<CardDefinition> Void { get; } = new();
    public Unit?[,] Board { get; } = new Unit?[Rules.Lanes, 2];
    public int TurnsTaken { get; set; }
    public int Essence { get; set; }
    public int Reserve { get; set; }
    public bool HasSpark { get; set; }
    public bool Mulliganed { get; set; }
    public int FatigueCount { get; set; }
    public bool HeroPowerUsed { get; set; }

    public Unit? At(Position p) => Board[p.Lane, (int)p.Row];
    public void Set(Position p, Unit? unit) => Board[p.Lane, (int)p.Row] = unit;

    public IEnumerable<(Position Pos, Unit Unit)> Units()
    {
        for (var lane = 0; lane < Rules.Lanes; lane++)
        {
            foreach (var row in new[] { Row.Front, Row.Back })
            {
                var u = Board[lane, (int)row];
                if (u is not null) yield return (new Position(lane, row), u);
            }
        }
    }

    public IEnumerable<Position> EmptyCells()
    {
        foreach (var row in new[] { Row.Front, Row.Back })
            for (var lane = 0; lane < Rules.Lanes; lane++)
                if (Board[lane, (int)row] is null) yield return new Position(lane, row);
    }

    public bool HasUnitInLane(int lane) => Board[lane, 0] is not null || Board[lane, 1] is not null;
}

public sealed class GameState
{
    public PlayerState[] Players { get; } = new PlayerState[2];
    public int ActivePlayer { get; set; }
    public int FirstPlayer { get; set; }
    /// <summary>Właściciel Kamienia Mocy na każdej ścieżce (null = neutralny).</summary>
    public int?[] Stones { get; } = new int?[Rules.Lanes];
    public CardDefinition?[] Lands { get; } = new CardDefinition?[Rules.Lanes];
    public int?[] LandOwners { get; } = new int?[Rules.Lanes];
    public GamePhase Phase { get; set; } = GamePhase.Mulligan;
    public int? Winner { get; set; }
    public bool IsDraw { get; set; }
    public WinReason? WinReason { get; set; }
    public List<string> Log { get; } = new();
    public int NextUid { get; set; } = 1;

    public PlayerState Active => Players[ActivePlayer];
    public PlayerState Opponent => Players[1 - ActivePlayer];
    public bool IsOver => Phase == GamePhase.Over;

    public int StonesOf(int player) => Stones.Count(s => s == player);

    public LandType LandIn(int lane) => Lands[lane]?.Land ?? LandType.None;

    public int MaxHealth(Unit unit, int lane) => unit.BaseHealth + (LandIn(lane) == LandType.Grove ? 2 : 0);

    public int RemainingHealth(Unit unit, int lane) => MaxHealth(unit, lane) - unit.Damage;
}
