using RozdarteKrainy.Engine;
using Xunit;

namespace RozdarteKrainy.Engine.Tests;

public class SimpleBotTests
{
    private static readonly CardLibrary Lib = CardLibrary.LoadDefault();

    [Fact]
    public void BotVsBot_GamesAlwaysFinish()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var game = Game.Create(Lib, "zakon-plomienia", "lesny-krag", seed);
            game.Begin();
            for (var guard = 0; !game.State.IsOver && guard < 100; guard++)
                SimpleBot.PlayTurn(game);
            Assert.True(game.State.IsOver, $"Partia z ziarnem {seed} się nie skończyła.");
        }
    }

    [Fact]
    public void Bot_PlaysCardsWhenItCan()
    {
        var game = Game.Create(Lib, "lesny-krag", "zakon-plomienia", seed: 3, firstPlayer: 0);
        game.Begin();
        game.State.Active.Essence = 10;
        var handBefore = game.State.Active.Hand.Count;
        SimpleBot.PlayTurn(game);
        Assert.True(game.State.Players[0].Hand.Count < handBefore);
    }
}
