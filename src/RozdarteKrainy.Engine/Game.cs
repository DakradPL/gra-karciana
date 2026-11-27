namespace RozdarteKrainy.Engine;

public sealed class GameSetup
{
    public CardDefinition Hero0 { get; init; } = null!;
    public List<CardDefinition> Deck0 { get; init; } = new();
    public CardDefinition Hero1 { get; init; } = null!;
    public List<CardDefinition> Deck1 { get; init; } = new();
    public int Seed { get; init; }
    public bool Shuffle { get; init; } = true;
    /// <summary>Kto zaczyna. Null = losowanie.</summary>
    public int? FirstPlayer { get; init; }
    /// <summary>Potrzebne do żetonów (Wilczek). Opcjonalne w testach.</summary>
    public CardLibrary? Library { get; init; }
}

/// <summary>
/// Silnik zasad. Jedyne miejsce, które zmienia stan gry. Deterministyczny dla danego ziarna,
/// więc serwer i klient mogą liczyć to samo.
/// </summary>
public sealed class Game
{
    private readonly Random _rng;
    private readonly CardLibrary? _library;

    public GameState State { get; } = new();

    public Game(GameSetup setup)
    {
        _rng = new Random(setup.Seed);
        _library = setup.Library;

        State.Players[0] = new PlayerState { Index = 0, Hero = setup.Hero0 };
        State.Players[1] = new PlayerState { Index = 1, Hero = setup.Hero1 };
        State.Players[0].Deck.AddRange(setup.Deck0);
        State.Players[1].Deck.AddRange(setup.Deck1);

        if (setup.Shuffle)
        {
            Shuffle(State.Players[0].Deck);
            Shuffle(State.Players[1].Deck);
        }

        State.FirstPlayer = setup.FirstPlayer ?? _rng.Next(2);
        State.ActivePlayer = State.FirstPlayer;

        var first = State.Players[State.FirstPlayer];
        var second = State.Players[1 - State.FirstPlayer];
        for (var i = 0; i < Rules.StartingHand; i++)
        {
            DrawCard(first);
            DrawCard(second);
        }
        DrawCard(second);
        second.HasSpark = true;
        Log($"Zaczyna gracz {State.FirstPlayer}.");
    }

    public static Game Create(CardLibrary library, string deck0, string deck1, int seed, int? firstPlayer = null)
    {
        var (hero0, cards0) = library.BuildDeck(deck0);
        var (hero1, cards1) = library.BuildDeck(deck1);
        return new Game(new GameSetup
        {
            Hero0 = hero0, Deck0 = cards0, Hero1 = hero1, Deck1 = cards1,
            Seed = seed, FirstPlayer = firstPlayer, Library = library,
        });
    }

    // ------------------------------------------------------------------ start partii

    /// <summary>Jednorazowa wymiana wybranych kart z ręki na początku partii.</summary>
    public ActionResult Mulligan(int player, IReadOnlyCollection<int> handIndices)
    {
        if (State.Phase != GamePhase.Mulligan) return ActionResult.Fail("Wymiana jest możliwa tylko przed pierwszą turą.");
        var p = State.Players[player];
        if (p.Mulliganed) return ActionResult.Fail("Ten gracz już wymienił karty.");
        if (handIndices.Any(i => i < 0 || i >= p.Hand.Count) || handIndices.Distinct().Count() != handIndices.Count)
            return ActionResult.Fail("Nieprawidłowe karty do wymiany.");

        var returned = handIndices.OrderByDescending(i => i).Select(i =>
        {
            var c = p.Hand[i];
            p.Hand.RemoveAt(i);
            return c;
        }).ToList();
        p.Deck.AddRange(returned);
        Shuffle(p.Deck);
        for (var i = 0; i < returned.Count; i++) DrawCard(p);
        p.Mulliganed = true;
        return ActionResult.Success;
    }

    /// <summary>Kończy fazę wymiany i rozpoczyna pierwszą turę.</summary>
    public void Begin()
    {
        if (State.Phase != GamePhase.Mulligan) return;
        State.Phase = GamePhase.Actions;
        StartTurn();
    }

    // ------------------------------------------------------------------ Świt

    private void StartTurn()
    {
        var p = State.Active;
        p.TurnsTaken++;
        Log($"Tura {p.TurnsTaken} gracza {p.Index}.");

        if (State.StonesOf(p.Index) == Rules.Lanes)
        {
            Win(p.Index, WinReason.Dominance);
            return;
        }

        p.Essence = Math.Min(p.TurnsTaken, Rules.MaxBaseEssence) + State.StonesOf(p.Index) + p.Reserve;
        p.Reserve = 0;
        p.HeroPowerUsed = false;

        foreach (var (_, unit) in p.Units())
        {
            unit.SummoningSick = false;
            unit.MovedThisTurn = false;
        }

        DrawCard(p);
        if (State.IsOver) return;

        // Pole Zgliszcz: 1 obrażenie każdej jednostce na ścieżce w Świcie każdego gracza.
        for (var lane = 0; lane < Rules.Lanes; lane++)
        {
            if (State.LandIn(lane) != LandType.Scorch) continue;
            foreach (var player in State.Players)
                foreach (var row in new[] { Row.Front, Row.Back })
                    if (player.Board[lane, (int)row] is { } u) u.Damage += 1;
        }
        RemoveDeadUnits();

        foreach (var (pos, unit) in p.Units().ToList())
        {
            if (unit.Card.Trigger == TriggerType.DawnSummonTokenInLane && unit.Card.TokenId is { } tokenId)
            {
                var cell = p.EmptyCells().Where(c => c.Lane == pos.Lane).Cast<Position?>().FirstOrDefault();
                if (cell is { } c) Summon(p, Token(tokenId), c, sick: true);
            }
        }
    }

    // ------------------------------------------------------------------ Działania

    public ActionResult PlayCard(int handIndex, Position? placement = null, Target? target = null)
    {
        if (CheckCanAct() is { } fail) return fail;
        var p = State.Active;
        if (handIndex < 0 || handIndex >= p.Hand.Count) return ActionResult.Fail("Nie ma takiej karty w ręce.");
        var card = p.Hand[handIndex];
        return PlayInternal(card, card.Cost, placement, target, () => p.Hand.RemoveAt(handIndex), fromVoid: false);
    }

    /// <summary>Echo X: zagranie karty z Otchłani za koszt Echa. Potem karta znika z gry.</summary>
    public ActionResult PlayFromVoid(int voidIndex, Position? placement = null, Target? target = null)
    {
        if (CheckCanAct() is { } fail) return fail;
        var p = State.Active;
        if (voidIndex < 0 || voidIndex >= p.Void.Count) return ActionResult.Fail("Nie ma takiej karty w Otchłani.");
        var card = p.Void[voidIndex];
        if (card.EchoCost is not { } echo) return ActionResult.Fail("Ta karta nie ma Echa.");
        return PlayInternal(card, echo, placement, target, () => p.Void.RemoveAt(voidIndex), fromVoid: true);
    }

    private ActionResult PlayInternal(CardDefinition card, int cost, Position? placement, Target? target,
                                      Action removeFromSource, bool fromVoid)
    {
        var p = State.Active;
        if (p.Essence < cost) return ActionResult.Fail($"Za mało Esencji ({p.Essence}/{cost}).");

        var error = ValidatePlay(card, placement, target);
        if (error is not null) return ActionResult.Fail(error);

        p.Essence -= cost;
        removeFromSource();
        Log($"Gracz {p.Index} zagrywa {card.Name}.");

        switch (card.Type)
        {
            case CardType.Unit:
            {
                var unit = Summon(p, card, placement!.Value, sick: !card.Keywords.Contains(Keyword.Charge));
                unit.Exiled = fromVoid;
                foreach (var effect in card.OnPlay) Resolve(effect, card, placement, target, unit, placement);
                break;
            }
            case CardType.Spell:
                foreach (var effect in card.OnPlay) Resolve(effect, card, placement, target, null, null);
                if (!fromVoid) p.Void.Add(card);
                break;
            case CardType.Artifact:
            {
                var host = UnitAt(target!.Value)!;
                foreach (var effect in card.OnPlay) Resolve(effect, card, placement, target, null, null);
                host.Attachments.Add(card);
                break;
            }
            case CardType.Land:
            {
                var lane = placement!.Value.Lane;
                if (State.Lands[lane] is { } old && State.LandOwners[lane] is { } oldOwner)
                    State.Players[oldOwner].Void.Add(old);
                State.Lands[lane] = card;
                State.LandOwners[lane] = p.Index;
                break;
            }
        }

        RemoveDeadUnits();
        return ActionResult.Success;
    }

    private string? ValidatePlay(CardDefinition card, Position? placement, Target? target)
    {
        var p = State.Active;
        switch (card.Type)
        {
            case CardType.Hero:
                return "Bohatera nie zagrywa się z ręki.";
            case CardType.Unit:
                if (placement is not { } pos || !pos.IsValid) return "Wybierz pole dla jednostki.";
                if (p.At(pos) is not null) return "To pole jest zajęte.";
                break;
            case CardType.Artifact:
                if (target is not { } t || t.Player != p.Index || UnitAt(t) is null)
                    return "Artefakt przypina się do własnej jednostki.";
                break;
            case CardType.Land:
                if (placement is not { } lp || !lp.IsValid) return "Wybierz ścieżkę dla Krainy.";
                break;
        }

        foreach (var effect in card.OnPlay)
        {
            var error = ValidateEffect(effect, placement, target);
            if (error is not null) return error;
        }
        return null;
    }

    private string? ValidateEffect(EffectDefinition effect, Position? placement, Target? target)
    {
        var me = State.ActivePlayer;
        switch (effect.Type)
        {
            case EffectType.DamageUnit:
                return IsTargetableUnit(target, me) ? null : "Wybierz jednostkę jako cel.";
            case EffectType.HealUnitOrOwnFortress:
                if (target is not { } h || h.Player != me) return "Wybierz własną jednostkę albo Twierdzę.";
                return h.IsFortress || UnitAt(h) is not null ? null : "Wybierz własną jednostkę albo Twierdzę.";
            case EffectType.BuffUnit:
                return target is { } b && b.Player == me && UnitAt(b) is not null ? null : "Wybierz własną jednostkę.";
            case EffectType.DamageEnemiesInLane:
                return placement is { } lane && lane.IsValid ? null : "Wybierz ścieżkę.";
            case EffectType.RootUnit:
                return target is { } r && r.Player != me && IsTargetableUnit(target, me) ? null : "Wybierz wrogą jednostkę.";
            case EffectType.RelocateEnemyUnitAndDraw:
                if (target is not { } m || m.Player == me || !IsTargetableUnit(target, me)) return "Wybierz wrogą jednostkę.";
                if (placement is not { } dest || !dest.IsValid || dest.Lane == m.Position!.Value.Lane)
                    return "Wybierz wolne pole na innej ścieżce.";
                return State.Players[m.Player].At(dest) is null ? null : "To pole jest zajęte.";
            case EffectType.SummonTokens:
                return _library is null ? "Brak biblioteki kart dla żetonów." : null;
            default:
                return null;
        }
    }

    private bool IsTargetableUnit(Target? target, int caster)
    {
        if (target is not { } t || t.IsFortress) return false;
        var unit = UnitAt(t);
        if (unit is null) return false;
        return unit.Owner == caster || !unit.Has(Keyword.Stealth);
    }

    private void Resolve(EffectDefinition effect, CardDefinition source, Position? placement, Target? target,
                         Unit? self, Position? selfPos)
    {
        var p = State.Active;
        switch (effect.Type)
        {
            case EffectType.DamageUnit:
                UnitAt(target!.Value)!.Damage += effect.Amount;
                break;

            case EffectType.DamageEnemyFortress:
            {
                var amount = effect.BondAmount is { } bond && selfPos is { } sp && HasBond(p, source, sp)
                    ? bond
                    : effect.Amount;
                DamageFortress(1 - p.Index, amount);
                break;
            }

            case EffectType.HealUnitOrOwnFortress:
            {
                var t = target!.Value;
                if (t.IsFortress) p.Fortress = Math.Min(Rules.FortressHealth, p.Fortress + effect.Amount);
                else
                {
                    var u = UnitAt(t)!;
                    u.Damage = Math.Max(0, u.Damage - effect.Amount);
                }
                break;
            }

            case EffectType.BuffUnit:
            {
                var u = UnitAt(target!.Value)!;
                u.BaseAttack += effect.Attack;
                u.BaseHealth += effect.Health;
                foreach (var k in effect.GrantKeywords) u.Keywords.Add(k);
                break;
            }

            case EffectType.BuffSelfIfBond:
                if (self is not null && selfPos is { } pos && HasBond(p, source, pos))
                {
                    self.BaseAttack += effect.Attack;
                    self.BaseHealth += effect.Health;
                }
                break;

            case EffectType.DamageEnemiesInLane:
            {
                var enemy = State.Opponent;
                var lane = placement!.Value.Lane;
                foreach (var row in new[] { Row.Front, Row.Back })
                    if (enemy.Board[lane, (int)row] is { } u) u.Damage += effect.Amount;
                break;
            }

            case EffectType.SummonTokens:
            {
                var token = Token(effect.TokenId!);
                for (var i = 0; i < effect.Amount; i++)
                {
                    var cell = p.EmptyCells().Cast<Position?>().FirstOrDefault();
                    if (cell is not { } c) break;
                    Summon(p, token, c, sick: true);
                }
                break;
            }

            case EffectType.RallyAllUnits:
                foreach (var (_, u) in p.Units())
                {
                    u.TempAttack += effect.Attack;
                    u.FreeMove = true;
                }
                break;

            case EffectType.RootUnit:
                UnitAt(target!.Value)!.Rooted = true;
                break;

            case EffectType.RelocateEnemyUnitAndDraw:
            {
                var t = target!.Value;
                var owner = State.Players[t.Player];
                var u = owner.At(t.Position!.Value)!;
                owner.Set(t.Position!.Value, null);
                owner.Set(placement!.Value, u);
                DrawCard(p);
                break;
            }
        }
    }

    /// <summary>Więź: twoja jednostka tej samej frakcji stoi na sąsiedniej ścieżce.</summary>
    private static bool HasBond(PlayerState p, CardDefinition source, Position pos)
    {
        return p.Units().Any(x => Math.Abs(x.Pos.Lane - pos.Lane) == 1 && x.Unit.Card.Faction == source.Faction);
    }

    public ActionResult MoveUnit(Position from, Position to)
    {
        if (CheckCanAct() is { } fail) return fail;
        var p = State.Active;
        if (!from.IsValid || !to.IsValid) return ActionResult.Fail("Nieprawidłowe pole.");
        var unit = p.At(from);
        if (unit is null) return ActionResult.Fail("Na tym polu nie ma twojej jednostki.");
        if (p.At(to) is not null) return ActionResult.Fail("Pole docelowe jest zajęte.");
        var adjacent = (from.Lane == to.Lane && from.Row != to.Row) ||
                       (Math.Abs(from.Lane - to.Lane) == 1 && from.Row == to.Row);
        if (!adjacent) return ActionResult.Fail("Ruch to jeden krok: Front/Tyły albo sąsiednia ścieżka.");
        if (unit.MovedThisTurn) return ActionResult.Fail("Ta jednostka już się ruszała w tej turze.");
        if (unit.Rooted) return ActionResult.Fail("Ta jednostka jest unieruchomiona.");
        var cost = unit.FreeMove ? 0 : Rules.MoveCost;
        if (p.Essence < cost) return ActionResult.Fail("Za mało Esencji na ruch.");

        p.Essence -= cost;
        unit.FreeMove = false;
        unit.MovedThisTurn = true;
        p.Set(from, null);
        p.Set(to, unit);
        RemoveDeadUnits();
        return ActionResult.Success;
    }

    public ActionResult UseHeroPower(Target target)
    {
        if (CheckCanAct() is { } fail) return fail;
        var p = State.Active;
        if (p.HeroPowerUsed) return ActionResult.Fail("Moc Bohatera była już użyta w tej turze.");
        if (p.Essence < Rules.HeroPowerCost) return ActionResult.Fail("Za mało Esencji na moc Bohatera.");

        switch (p.Hero.HeroPower)
        {
            case HeroPowerType.FlameStrike:
            {
                if (!target.IsFortress && !IsTargetableUnit(target, p.Index))
                    return ActionResult.Fail("Wybierz jednostkę albo Twierdzę.");
                var awakened = State.Players[1 - p.Index].Fortress <= Rules.HeroAwakenFortressThreshold;
                var amount = awakened ? 2 : 1;
                Pay();
                if (target.IsFortress) DamageFortress(target.Player, amount);
                else UnitAt(target)!.Damage += amount;
                break;
            }
            case HeroPowerType.ForestBlessing:
            {
                if (target.Player != p.Index || UnitAt(target) is not { } u)
                    return ActionResult.Fail("Wybierz własną jednostkę.");
                Pay();
                u.BaseAttack += 1;
                u.BaseHealth += 1;
                if (State.StonesOf(p.Index) >= Rules.HeroAwakenStones && _library is not null && p.Hero.TokenId is { } tokenId)
                {
                    var lane = target.Position!.Value.Lane;
                    var cell = p.EmptyCells().OrderBy(c => c.Lane == lane ? 0 : 1).Cast<Position?>().FirstOrDefault();
                    if (cell is { } c) Summon(p, Token(tokenId), c, sick: true);
                }
                break;
            }
            default:
                return ActionResult.Fail("Ten Bohater nie ma mocy.");
        }

        RemoveDeadUnits();
        return ActionResult.Success;

        void Pay()
        {
            p.Essence -= Rules.HeroPowerCost;
            p.HeroPowerUsed = true;
        }
    }

    /// <summary>Iskra drugiego gracza: raz w partii +1 Esencji.</summary>
    public ActionResult UseSpark()
    {
        if (CheckCanAct() is { } fail) return fail;
        var p = State.Active;
        if (!p.HasSpark) return ActionResult.Fail("Nie masz Iskry.");
        p.HasSpark = false;
        p.Essence += 1;
        return ActionResult.Success;
    }

    // ------------------------------------------------------------------ Starcie i Zmierzch

    public ActionResult EndTurn()
    {
        if (CheckCanAct() is { } fail) return fail;
        var p = State.Active;

        Combat();
        if (State.IsOver) return ActionResult.Success;
        Dusk();

        foreach (var (_, u) in p.Units())
        {
            u.TempAttack = 0;
            u.FreeMove = false;
            u.Rooted = false;
        }
        p.Reserve = p.Essence > 0 ? 1 : 0;
        p.Essence = 0;

        if (State.Players[0].TurnsTaken >= Rules.TurnLimit && State.Players[1].TurnsTaken >= Rules.TurnLimit)
        {
            var f0 = State.Players[0].Fortress;
            var f1 = State.Players[1].Fortress;
            if (f0 == f1)
            {
                State.IsDraw = true;
                State.Phase = GamePhase.Over;
                State.WinReason = WinReason.TurnLimit;
                Log("Remis po limicie tur.");
            }
            else Win(f0 > f1 ? 0 : 1, WinReason.TurnLimit);
            return ActionResult.Success;
        }

        State.ActivePlayer = 1 - State.ActivePlayer;
        StartTurn();
        return ActionResult.Success;
    }

    private void Combat()
    {
        var me = State.Active;
        var enemy = State.Opponent;

        for (var lane = 0; lane < Rules.Lanes; lane++)
        {
            foreach (var row in new[] { Row.Front, Row.Back })
            {
                var attacker = me.Board[lane, (int)row];
                if (attacker is null) continue;
                if (row == Row.Back && !attacker.Has(Keyword.Ranged)) continue;
                if (attacker.SummoningSick || attacker.Rooted || attacker.Attack <= 0) continue;

                Attack(attacker, lane, enemy);
                RemoveDeadUnits();
                if (State.IsOver) return;
            }
        }
    }

    private void Attack(Unit attacker, int lane, PlayerState enemy)
    {
        attacker.Keywords.Remove(Keyword.Stealth);
        var front = enemy.Board[lane, (int)Row.Front];
        var back = enemy.Board[lane, (int)Row.Back];
        if (front is not null && front.Has(Keyword.Stealth)) front = null;
        if (back is not null && back.Has(Keyword.Stealth)) back = null;

        Unit? target;
        Row targetRow;
        if (front is not null && front.Has(Keyword.Guard)) { target = front; targetRow = Row.Front; }
        else if (back is not null && back.Has(Keyword.Guard)) { target = back; targetRow = Row.Back; }
        else if (front is not null) { target = front; targetRow = Row.Front; }
        else if (back is not null) { target = back; targetRow = Row.Back; }
        else
        {
            Log($"{attacker.Card.Name} uderza w Twierdzę za {attacker.Attack}.");
            DamageFortress(enemy.Index, attacker.Attack);
            return;
        }

        var damage = attacker.Attack;
        var remaining = State.RemainingHealth(target, lane);
        target.Damage += damage;
        Log($"{attacker.Card.Name} atakuje {target.Card.Name} za {damage}.");

        if (targetRow == Row.Front && !attacker.Has(Keyword.Ranged))
            attacker.Damage += target.Attack;

        var excess = damage - remaining;
        if (excess > 0 && attacker.Has(Keyword.Pierce))
        {
            var behind = targetRow == Row.Front ? enemy.Board[lane, (int)Row.Back] : null;
            if (behind is not null) behind.Damage += excess;
            else DamageFortress(enemy.Index, excess);
        }
    }

    private void Dusk()
    {
        var me = State.Active;
        var enemy = State.Opponent;

        for (var lane = 0; lane < Rules.Lanes; lane++)
        {
            var mine = me.Board[lane, (int)Row.Front] is not null;
            var theirsFront = enemy.Board[lane, (int)Row.Front] is not null;
            var warden = enemy.Units().Any(x => x.Pos.Lane == lane && x.Unit.Has(Keyword.StoneWarden));
            if (mine && !theirsFront && !warden && State.Stones[lane] != me.Index)
            {
                State.Stones[lane] = me.Index;
                Log($"Gracz {me.Index} przejmuje Kamień Mocy na ścieżce {lane}.");
            }
        }

        foreach (var (pos, unit) in me.Units())
        {
            if (unit.Card.Trigger == TriggerType.DuskGrowIfStoneHeld && State.Stones[pos.Lane] == me.Index)
            {
                unit.BaseAttack += 1;
                unit.BaseHealth += 1;
            }
        }
    }

    // ------------------------------------------------------------------ pomocnicze

    private ActionResult? CheckCanAct()
    {
        if (State.Phase == GamePhase.Mulligan) return ActionResult.Fail("Partia jeszcze się nie zaczęła.");
        if (State.IsOver) return ActionResult.Fail("Partia jest zakończona.");
        return null;
    }

    private Unit? UnitAt(Target target) =>
        target.Position is { } pos && pos.IsValid ? State.Players[target.Player].At(pos) : null;

    private CardDefinition Token(string id) =>
        (_library ?? throw new InvalidOperationException("Brak biblioteki kart dla żetonów.")).Get(id);

    private Unit Summon(PlayerState p, CardDefinition card, Position pos, bool sick)
    {
        var unit = new Unit
        {
            Uid = State.NextUid++,
            Card = card,
            Owner = p.Index,
            BaseAttack = card.Attack,
            BaseHealth = card.Health,
            SummoningSick = sick,
        };
        foreach (var k in card.Keywords) unit.Keywords.Add(k);
        p.Set(pos, unit);
        return unit;
    }

    private void DrawCard(PlayerState p)
    {
        if (p.Deck.Count == 0)
        {
            p.FatigueCount++;
            Log($"Gracz {p.Index} ma pustą talię: Wyczerpanie {2 * p.FatigueCount}.");
            DamageFortress(p.Index, 2 * p.FatigueCount);
            return;
        }
        var card = p.Deck[0];
        p.Deck.RemoveAt(0);
        if (p.Hand.Count >= Rules.HandLimit) p.Void.Add(card);
        else p.Hand.Add(card);
    }

    private void DamageFortress(int player, int amount)
    {
        if (State.IsOver || amount <= 0) return;
        var p = State.Players[player];
        p.Fortress -= amount;
        if (p.Fortress <= 0) Win(1 - player, WinReason.Demolition);
    }

    private void RemoveDeadUnits()
    {
        foreach (var p in State.Players)
        {
            foreach (var (pos, unit) in p.Units().ToList())
            {
                if (unit.Damage < State.MaxHealth(unit, pos.Lane)) continue;
                p.Set(pos, null);
                if (!unit.Card.IsToken && !unit.Exiled) p.Void.Add(unit.Card);
                p.Void.AddRange(unit.Attachments);
                Log($"{unit.Card.Name} ginie.");
            }
        }
    }

    private void Win(int player, WinReason reason)
    {
        if (State.IsOver) return;
        State.Winner = player;
        State.WinReason = reason;
        State.Phase = GamePhase.Over;
        Log($"Wygrywa gracz {player} ({reason}).");
    }

    private void Shuffle(List<CardDefinition> cards)
    {
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }

    private void Log(string message) => State.Log.Add(message);
}
