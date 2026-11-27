using RozdarteKrainy.Engine;
using Xunit;

namespace RozdarteKrainy.Engine.Tests;

public class GameRulesTests
{
    private static readonly CardLibrary Lib = CardLibrary.LoadDefault();

    /// <summary>Partia Zakon (gracz 0) kontra Krąg (gracz 1), zaczyna gracz 0, plansza pusta.</summary>
    private static Game NewGame()
    {
        var game = Game.Create(Lib, "zakon-plomienia", "lesny-krag", seed: 1, firstPlayer: 0);
        game.Begin();
        game.State.Active.Essence = 10;
        return game;
    }

    private static int Give(Game g, string id)
    {
        g.State.Active.Hand.Add(Lib.Get(id));
        return g.State.Active.Hand.Count - 1;
    }

    private static Unit Place(Game g, int player, string id, int lane, Row row)
    {
        var card = Lib.Get(id);
        var unit = new Unit
        {
            Uid = g.State.NextUid++, Card = card, Owner = player,
            BaseAttack = card.Attack, BaseHealth = card.Health,
        };
        foreach (var k in card.Keywords) unit.Keywords.Add(k);
        g.State.Players[player].Set(new Position(lane, row), unit);
        return unit;
    }

    private static void Ok(ActionResult r) => Assert.True(r.Ok, r.Error);

    [Theory]
    [InlineData("zakon-plomienia")]
    [InlineData("lesny-krag")]
    public void StarterDecks_AreValid(string deckId)
    {
        var (hero, cards) = Lib.BuildDeck(deckId);
        Assert.Empty(DeckValidator.Validate(hero, cards));
        Assert.Equal(30, cards.Count);
    }

    [Fact]
    public void SecondPlayer_GetsFifthCardAndSpark()
    {
        var g = Game.Create(Lib, "zakon-plomienia", "lesny-krag", seed: 7, firstPlayer: 0);
        Assert.Equal(4, g.State.Players[0].Hand.Count);
        Assert.Equal(5, g.State.Players[1].Hand.Count);
        Assert.True(g.State.Players[1].HasSpark);
        Assert.False(g.State.Players[0].HasSpark);
        g.Begin();
        Assert.Equal(5, g.State.Players[0].Hand.Count);
    }

    [Fact]
    public void Essence_GrowsEachTurn_AndKeepsOneAsReserve()
    {
        var g = Game.Create(Lib, "zakon-plomienia", "lesny-krag", seed: 1, firstPlayer: 0);
        g.Begin();
        Assert.Equal(1, g.State.Players[0].Essence);
        Ok(g.EndTurn());
        Ok(g.EndTurn());
        Assert.Equal(3, g.State.Players[0].Essence); // 2 za turę + 1 Zapas
    }

    [Fact]
    public void Essence_BaseIsCappedAtSeven_PlusStones()
    {
        var g = NewGame();
        g.State.Players[0].TurnsTaken = 11;
        g.State.Stones[2] = 0;
        Ok(g.EndTurn());
        Ok(g.EndTurn());
        Assert.Equal(7 + 1 + 1, g.State.Players[0].Essence); // baza 7, Kamień, Zapas
    }

    [Fact]
    public void UnitWithoutCharge_WaitsOneTurnBeforeAttacking()
    {
        var g = NewGame();
        Ok(g.PlayCard(Give(g, "giermek-zakonu"), new Position(0, Row.Front)));
        Ok(g.EndTurn());
        Assert.Equal(25, g.State.Players[1].Fortress);
        Ok(g.EndTurn());
        Ok(g.EndTurn());
        Assert.Equal(23, g.State.Players[1].Fortress);
    }

    [Fact]
    public void Charge_AttacksTheFortressImmediately()
    {
        var g = NewGame();
        Ok(g.PlayCard(Give(g, "plomienny-zwiadowca"), new Position(1, Row.Front)));
        Ok(g.EndTurn());
        Assert.Equal(24, g.State.Players[1].Fortress);
    }

    [Fact]
    public void FrontDefender_StrikesBack()
    {
        var g = NewGame();
        Place(g, 1, "lesny-wilczek", 0, Row.Front);
        Ok(g.PlayCard(Give(g, "jezdziec-pozogi"), new Position(0, Row.Front)));
        Ok(g.EndTurn());
        Assert.Null(g.State.Players[1].At(new Position(0, Row.Front)));
        Assert.Contains(g.State.Players[1].Void, c => c.Id == "lesny-wilczek");
        Assert.Equal(1, g.State.Players[0].At(new Position(0, Row.Front))!.Damage);
    }

    [Fact]
    public void Ranged_AttacksFromBack_WithoutTakingDamage()
    {
        var g = NewGame();
        var archer = Place(g, 0, "lucznik-zaru", 0, Row.Back);
        var boar = Place(g, 1, "dzik-puszczy", 0, Row.Front);
        Ok(g.EndTurn());
        Assert.Equal(2, boar.Damage);
        Assert.Equal(0, archer.Damage);
    }

    [Fact]
    public void Guard_MustBeAttackedFirst()
    {
        var g = NewGame();
        Place(g, 0, "giermek-zakonu", 1, Row.Front);
        var wolf = Place(g, 1, "lesny-wilczek", 1, Row.Front);
        var vine = Place(g, 1, "pnacze-straznicze", 1, Row.Back);
        Ok(g.EndTurn());
        Assert.Equal(2, vine.Damage);
        Assert.Equal(0, wolf.Damage);
    }

    [Fact]
    public void Pierce_CarriesExcessDamageIntoFortress()
    {
        var g = NewGame();
        Place(g, 0, "taran-oblezniczy", 2, Row.Front);
        Place(g, 1, "lesny-wilczek", 2, Row.Front);
        Ok(g.EndTurn());
        Assert.Equal(22, g.State.Players[1].Fortress);
    }

    [Fact]
    public void Dusk_CapturesStone_WhenRivalFrontIsEmpty()
    {
        var g = NewGame();
        Place(g, 0, "giermek-zakonu", 0, Row.Front);
        Ok(g.EndTurn());
        Assert.Equal(0, g.State.Stones[0]);
        Assert.Null(g.State.Stones[1]);
    }

    [Fact]
    public void StoneWarden_BlocksCapture()
    {
        var g = NewGame();
        Place(g, 0, "paladyn-brzasku", 1, Row.Front);
        Place(g, 1, "pnacze-straznicze", 1, Row.Back);
        Ok(g.EndTurn());
        Assert.Null(g.State.Stones[1]);
    }

    [Fact]
    public void Dominance_WinsAtDawn()
    {
        var g = NewGame();
        g.State.Stones[0] = 0;
        g.State.Stones[1] = 0;
        g.State.Stones[2] = 0;
        Ok(g.EndTurn());
        Assert.False(g.State.IsOver);
        Ok(g.EndTurn());
        Assert.True(g.State.IsOver);
        Assert.Equal(0, g.State.Winner);
        Assert.Equal(WinReason.Dominance, g.State.WinReason);
    }

    [Fact]
    public void Fatigue_Escalates()
    {
        var g = NewGame();
        g.State.Players[1].Deck.Clear();
        Ok(g.EndTurn());
        Assert.Equal(23, g.State.Players[1].Fortress);
        Ok(g.EndTurn());
        Ok(g.EndTurn());
        Assert.Equal(19, g.State.Players[1].Fortress);
    }

    [Fact]
    public void Move_CostsEssence_AndOnlyOncePerTurn()
    {
        var g = NewGame();
        Place(g, 0, "giermek-zakonu", 0, Row.Front);
        g.State.Active.Essence = 3;
        Assert.False(g.MoveUnit(new Position(0, Row.Front), new Position(2, Row.Front)).Ok);
        Ok(g.MoveUnit(new Position(0, Row.Front), new Position(1, Row.Front)));
        Assert.Equal(2, g.State.Active.Essence);
        Assert.False(g.MoveUnit(new Position(1, Row.Front), new Position(1, Row.Back)).Ok);
    }

    [Fact]
    public void Bond_StrengthensFirePriest()
    {
        var g = NewGame();
        Ok(g.PlayCard(Give(g, "kaplan-ognia"), new Position(0, Row.Back)));
        Assert.Equal(24, g.State.Players[1].Fortress);
        Ok(g.PlayCard(Give(g, "kaplan-ognia"), new Position(1, Row.Back)));
        Assert.Equal(22, g.State.Players[1].Fortress);
    }

    [Fact]
    public void Spell_KillsUnit_AndGoesToVoid()
    {
        var g = NewGame();
        Place(g, 1, "lesny-wilczek", 0, Row.Front);
        Ok(g.PlayCard(Give(g, "iskra-gniewu"), target: Target.UnitAt(1, 0, Row.Front)));
        Assert.Null(g.State.Players[1].At(new Position(0, Row.Front)));
        Assert.Contains(g.State.Players[0].Void, c => c.Id == "iskra-gniewu");
    }

    [Fact]
    public void NotEnoughEssence_IsRejected_WithoutChanges()
    {
        var g = NewGame();
        g.State.Active.Essence = 2;
        var idx = Give(g, "paladyn-brzasku");
        var handBefore = g.State.Active.Hand.Count;
        Assert.False(g.PlayCard(idx, new Position(0, Row.Front)).Ok);
        Assert.Equal(handBefore, g.State.Active.Hand.Count);
        Assert.Equal(2, g.State.Active.Essence);
    }

    [Fact]
    public void HeroPower_OncePerTurn()
    {
        var g = NewGame();
        Ok(g.UseHeroPower(Target.Fortress(1)));
        Assert.Equal(24, g.State.Players[1].Fortress);
        Assert.False(g.UseHeroPower(Target.Fortress(1)).Ok);
    }

    [Fact]
    public void WolfPack_SummonsTwoTokens()
    {
        var g = NewGame();
        Ok(g.EndTurn());
        g.State.Active.Essence = 10;
        Ok(g.PlayCard(Give(g, "wilcza-wataha")));
        Assert.Equal(2, g.State.Players[1].Units().Count(x => x.Unit.Card.Id == "wilczek"));
    }

    [Fact]
    public void Echo_PlaysFromVoid_ThenLeavesTheGame()
    {
        var g = NewGame();
        g.State.Active.Void.Add(Lib.Get("feniks-zakonu"));
        Ok(g.PlayFromVoid(0, new Position(0, Row.Front)));
        Assert.Empty(g.State.Active.Void);
        Ok(g.PlayCard(Give(g, "iskra-gniewu"), target: Target.UnitAt(0, 0, Row.Front)));
        Ok(g.PlayCard(Give(g, "iskra-gniewu"), target: Target.UnitAt(0, 0, Row.Front)));
        Ok(g.PlayCard(Give(g, "iskra-gniewu"), target: Target.UnitAt(0, 0, Row.Front)));
        Assert.Null(g.State.Active.At(new Position(0, Row.Front)));
        Assert.DoesNotContain(g.State.Active.Void, c => c.Id == "feniks-zakonu");
    }

    [Fact]
    public void Grove_AddsHealth_WhileUnitStaysInLane()
    {
        var g = NewGame();
        var squire = Place(g, 0, "giermek-zakonu", 0, Row.Front);
        squire.Damage = 1; // 2/1 z 1 obrażeniem przeżywa tylko dzięki Gajowi
        g.State.Lands[0] = Lib.Get("swiety-gaj");
        g.State.LandOwners[0] = 1;
        Ok(g.MoveUnit(new Position(0, Row.Front), new Position(0, Row.Back)));
        Assert.NotNull(g.State.Active.At(new Position(0, Row.Back)));
        Ok(g.EndTurn());
        Ok(g.EndTurn());
        Ok(g.MoveUnit(new Position(0, Row.Back), new Position(1, Row.Back)));
        Assert.Null(g.State.Active.At(new Position(1, Row.Back)));
    }

    [Fact]
    public void TurnLimit_HigherFortressWins()
    {
        var g = NewGame();
        g.State.Players[0].TurnsTaken = 20;
        g.State.Players[1].TurnsTaken = 20;
        g.State.Players[1].Fortress = 10;
        Ok(g.EndTurn());
        Assert.Equal(0, g.State.Winner);
        Assert.Equal(WinReason.TurnLimit, g.State.WinReason);
    }

    [Fact]
    public void SameSeed_GivesSameGame()
    {
        var a = Game.Create(Lib, "zakon-plomienia", "lesny-krag", seed: 42);
        var b = Game.Create(Lib, "zakon-plomienia", "lesny-krag", seed: 42);
        Assert.Equal(a.State.FirstPlayer, b.State.FirstPlayer);
        Assert.Equal(a.State.Players[0].Hand.Select(c => c.Id), b.State.Players[0].Hand.Select(c => c.Id));
        Assert.Equal(a.State.Players[1].Deck.Select(c => c.Id), b.State.Players[1].Deck.Select(c => c.Id));
    }
}
