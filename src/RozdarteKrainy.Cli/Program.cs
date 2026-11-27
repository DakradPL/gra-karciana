using System.Text;
using RozdarteKrainy.Engine;

namespace RozdarteKrainy.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.InputEncoding = Encoding.UTF8; } catch (IOException) { } catch (PlatformNotSupportedException) { }

        var lib = CardLibrary.LoadDefault();

        if (args.Length > 0 && (args[0] == "--symulacja" || args[0] == "-s"))
        {
            var games = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 200;
            Simulation.Run(lib, games);
            return 0;
        }

        Console.WriteLine("ROZDARTE KRAINY · wersja testowa w terminalu");
        Console.WriteLine("Wybierz talię:  1) Zakon Płomienia (agresja)   2) Leśny Krąg (kontrola Kamieni)");
        Console.Write("> ");
        var choice = Console.ReadLine()?.Trim();
        var (human, bot) = choice == "2" ? ("lesny-krag", "zakon-plomienia") : ("zakon-plomienia", "lesny-krag");

        new Session(lib, human, bot, Environment.TickCount).Run();
        return 0;
    }
}

internal sealed class Session
{
    private const int Human = 0;
    private const int Bot = 1;
    private const int CellWidth = 22;
    private static readonly string[] LaneNames = { "LEWA (L)", "ŚRODKOWA (S)", "PRAWA (P)" };

    private readonly Game _game;
    private int _logIndex;

    public Session(CardLibrary lib, string humanDeck, string botDeck, int seed)
    {
        _game = Game.Create(lib, humanDeck, botDeck, seed);
    }

    public void Run()
    {
        var s = _game.State;
        Console.WriteLine(s.FirstPlayer == Human
            ? "Zaczynasz ty."
            : "Zaczyna bot. Dostajesz piątą kartę i Iskrę (raz w partii +1 Esencji, komenda i).");
        Console.WriteLine("Wpisz ? w dowolnym momencie, żeby zobaczyć komendy.");

        Mulligan();
        _game.Begin();
        _logIndex = s.Log.Count;

        while (!s.IsOver)
        {
            if (s.ActivePlayer == Bot)
            {
                Console.WriteLine();
                Console.WriteLine("--- Tura bota ---");
                SimpleBot.PlayTurn(_game);
                FlushLog();
                continue;
            }

            Render();
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) return;
            if (!Handle(line.Trim())) return;
            FlushLog();
        }

        Render();
        Console.WriteLine();
        if (s.IsDraw) Console.WriteLine("REMIS.");
        else Console.WriteLine(s.Winner == Human ? "WYGRAŁEŚ!" : "Przegrana. Wygrywa bot.");
        Console.WriteLine($"Sposób: {ReasonName(s.WinReason)}.");
    }

    private void Mulligan()
    {
        var me = _game.State.Players[Human];
        Console.WriteLine();
        Console.WriteLine("Twoja ręka startowa:");
        PrintHand(me);
        Console.WriteLine("Podaj numery kart do wymiany (np. 0 2) albo naciśnij Enter, żeby zostawić.");
        Console.Write("> ");
        var line = Console.ReadLine() ?? "";
        var indices = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => int.TryParse(x, out var i) ? i : -1).ToList();
        if (indices.Count == 0) return;
        var result = _game.Mulligan(Human, indices);
        if (!result.Ok) Console.WriteLine($"Nie wymieniono: {result.Error}");
    }

    // ------------------------------------------------------------------ komendy

    private bool Handle(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        var me = _game.State.Players[Human];

        switch (parts[0].ToLowerInvariant())
        {
            case "q":
                return false;
            case "?":
            case "h":
            case "pomoc":
                Help();
                break;
            case "k":
                Report(_game.EndTurn());
                break;
            case "i":
                Report(_game.UseSpark());
                break;
            case "z":
            case "e":
            {
                var fromVoid = parts[0].ToLowerInvariant() == "e";
                var list = fromVoid ? me.Void : me.Hand;
                if (parts.Length < 2 || !int.TryParse(parts[1], out var index) || index < 0 || index >= list.Count)
                {
                    Error("Podaj numer karty, np. z 0 LF.");
                    break;
                }
                if (!TryParseArgs(parts.Skip(2), out var args)) break;
                var (placement, target) = MapArgs(list[index], args);
                Report(fromVoid ? _game.PlayFromVoid(index, placement, target) : _game.PlayCard(index, placement, target));
                break;
            }
            case "r":
            {
                if (parts.Length != 3 || !TryParseArgs(parts.Skip(1), out var args) ||
                    args[0].Position is not { } from || args[1].Position is not { } to)
                {
                    Error("Użycie: r <skąd> <dokąd>, np. r LF SF.");
                    break;
                }
                Report(_game.MoveUnit(from, to));
                break;
            }
            case "m":
            {
                if (parts.Length != 2 || !TryParseArgs(parts.Skip(1), out var args))
                {
                    Error("Użycie: m <cel>, np. m !tw albo m LF.");
                    break;
                }
                Report(_game.UseHeroPower(args[0]));
                break;
            }
            default:
                Error("Nieznana komenda. Wpisz ? po pomoc.");
                break;
        }
        return true;
    }

    /// <summary>Zamienia argumenty komendy na pole i cel zgodnie z typem karty.</summary>
    private static (Position? Placement, Target? Target) MapArgs(CardDefinition card, List<Target> args)
    {
        Position? first = args.Count > 0 ? args[0].Position : null;
        Position? second = args.Count > 1 ? args[1].Position : null;
        Target? firstTarget = args.Count > 0 ? args[0] : null;
        Target? secondTarget = args.Count > 1 ? args[1] : null;

        switch (card.Type)
        {
            case CardType.Unit:
                return (first, secondTarget);
            case CardType.Land:
                return (first, null);
            case CardType.Artifact:
                return (null, firstTarget);
        }

        var effect = card.OnPlay.FirstOrDefault()?.Type;
        return effect switch
        {
            EffectType.DamageEnemiesInLane => (first, null),
            EffectType.RelocateEnemyUnitAndDraw => (second, firstTarget),
            _ => (null, firstTarget),
        };
    }

    private static bool TryParseArgs(IEnumerable<string> raw, out List<Target> args)
    {
        args = new List<Target>();
        foreach (var a in raw)
        {
            if (!TryParseTarget(a, out var t))
            {
                Error($"Nie rozumiem \"{a}\". Pola: LF, LT, SF, ST, PF, PT; rywal z !, Twierdza: tw / !tw.");
                return false;
            }
            args.Add(t);
        }
        return true;
    }

    private static bool TryParseTarget(string text, out Target target)
    {
        target = default;
        var enemy = text.StartsWith('!');
        var body = (enemy ? text[1..] : text).ToUpperInvariant();
        var player = enemy ? Bot : Human;

        if (body == "TW")
        {
            target = Target.Fortress(player);
            return true;
        }
        if (body.Length is < 1 or > 2) return false;

        var lane = body[0] switch { 'L' => 0, 'S' => 1, 'P' => 2, _ => -1 };
        if (lane < 0) return false;

        var row = Row.Front;
        if (body.Length == 2)
        {
            if (body[1] == 'F') row = Row.Front;
            else if (body[1] == 'T') row = Row.Back;
            else return false;
        }
        target = new Target(player, new Position(lane, row));
        return true;
    }

    // ------------------------------------------------------------------ wyświetlanie

    private void Render()
    {
        var s = _game.State;
        var me = s.Players[Human];
        var foe = s.Players[Bot];

        Console.WriteLine();
        Console.WriteLine($"==================== TWOJA TURA {me.TurnsTaken} ====================");
        Console.WriteLine($"BOT  {foe.Hero.Name} · Twierdza {foe.Fortress} · ręka {foe.Hand.Count} · talia {foe.Deck.Count}");
        Console.WriteLine();
        Console.WriteLine(Pad("", 8) + string.Join(" ", LaneNames.Select(n => Pad(n, CellWidth + 2))));
        if (s.Lands.Any(l => l is not null))
            Console.WriteLine(Pad("Kraina", 8) + string.Join(" ", Enumerable.Range(0, Rules.Lanes)
                .Select(l => Pad(s.Lands[l]?.Name ?? "", CellWidth + 2))));
        PrintRow("Tyły", foe, Row.Back);
        PrintRow("Front", foe, Row.Front);
        Console.WriteLine(Pad("Kamień", 8) + string.Join(" ", Enumerable.Range(0, Rules.Lanes)
            .Select(l => Pad(s.Stones[l] switch { Human => "  <<< TWÓJ >>>", Bot => "  <<< BOTA >>>", _ => "  (neutralny)" }, CellWidth + 2))));
        PrintRow("Front", me, Row.Front);
        PrintRow("Tyły", me, Row.Back);
        Console.WriteLine();

        var spark = me.HasSpark ? " · Iskra dostępna" : "";
        var power = me.HeroPowerUsed ? "użyta" : $"{Rules.HeroPowerCost} Esencji";
        Console.WriteLine($"TY   {me.Hero.Name} · Twierdza {me.Fortress} · ESENCJA {me.Essence}{spark} · talia {me.Deck.Count}");
        Console.WriteLine($"     Moc Bohatera ({power}): {me.Hero.Text}");
        Console.WriteLine("Ręka:");
        PrintHand(me);

        var echoes = me.Void.Select((c, i) => (c, i)).Where(x => x.c.EchoCost is not null).ToList();
        foreach (var (card, index) in echoes)
            Console.WriteLine($"  Otchłań {index}) Echo [{card.EchoCost}] {card.Name} (komenda e {index} <pole>)");
        Console.WriteLine("Komendy: z <nr> <pole>, r <skąd> <dokąd>, m <cel>, i, k = koniec tury, ? = pomoc");
    }

    private void PrintRow(string label, PlayerState p, Row row)
    {
        var cells = Enumerable.Range(0, Rules.Lanes).Select(lane =>
        {
            var u = p.Board[lane, (int)row];
            var text = u is null ? "" : UnitText(u, lane);
            return "[" + Pad(text, CellWidth) + "]";
        });
        Console.WriteLine(Pad(label, 8) + string.Join(" ", cells));
    }

    private string UnitText(Unit u, int lane)
    {
        var flags = new StringBuilder();
        if (u.Has(Keyword.Guard)) flags.Append('S');
        if (u.Has(Keyword.Ranged)) flags.Append('Z');
        if (u.Has(Keyword.Pierce)) flags.Append('P');
        if (u.Has(Keyword.StoneWarden)) flags.Append('K');
        if (u.Has(Keyword.Stealth)) flags.Append('U');
        if (u.SummoningSick) flags.Append('z');
        if (u.Rooted) flags.Append('#');
        var stats = $" {u.Attack}/{_game.State.RemainingHealth(u, lane)}";
        var tail = flags.Length > 0 ? stats + " " + flags : stats;
        var name = u.Card.Name;
        var room = CellWidth - tail.Length;
        if (name.Length > room) name = name[..Math.Max(1, room - 1)] + ".";
        return name + tail;
    }

    private static void PrintHand(PlayerState p)
    {
        for (var i = 0; i < p.Hand.Count; i++)
        {
            var c = p.Hand[i];
            var stats = c.Type == CardType.Unit ? $" {c.Attack}/{c.Health}" : "";
            var text = string.IsNullOrEmpty(c.Text) ? "" : $" · {c.Text}";
            Console.WriteLine($"  {i}) [{c.Cost}] {c.Name} · {TypeName(c.Type)}{stats}{text}");
        }
    }

    private void FlushLog()
    {
        var log = _game.State.Log;
        for (; _logIndex < log.Count; _logIndex++)
        {
            var line = log[_logIndex]
                .Replace("Gracz 0", "[Ty]").Replace("gracza 0", "[Ty]").Replace("gracz 0", "[Ty]")
                .Replace("Gracz 1", "[Bot]").Replace("gracza 1", "[Bot]").Replace("gracz 1", "[Bot]");
            Console.WriteLine("  · " + line);
        }
    }

    private static void Help()
    {
        Console.WriteLine("""

        POLA: L / S / P = lewa / środkowa / prawa ścieżka, F / T = Front / Tyły, np. LF, ST, PT.
              Pole rywala poprzedź znakiem !, np. !SF. Twierdza: tw (twoja), !tw (rywala).

        z <nr> <pole>        zagraj jednostkę z ręki, np.  z 0 LF
        z <nr> <cel>         zaklęcie lub artefakt na cel, np.  z 2 !SF   z 3 LF
        z <nr> <ścieżka>     Kraina albo Ognista Fala, np.  z 4 S
        z <nr> <pole> tw     Druidka Gaju: jednostka i leczenie twojej Twierdzy
        z <nr> !LF !PF       Szept Lasu: wroga jednostka i jej nowe pole
        e <nr> <pole>        zagraj kartę z Otchłani przez Echo
        r <skąd> <dokąd>     przesuń jednostkę (1 Esencja, 1 krok), np.  r LF SF
        m <cel>              moc Bohatera, np.  m !tw   m !LF   m LF
        i                    użyj Iskry (+1 Esencji, raz w partii)
        k                    zakończ turę: Starcie i Zmierzch
        q                    wyjdź

        Oznaczenia jednostek: S Straż, Z Zasięg, P Przebicie, K Strażnik Kamienia, U Ukrycie,
                              z zagrana w tej turze (nie atakuje), # unieruchomiona.
        Wygrywasz, gdy Twierdza rywala spadnie do 0 albo gdy w swoim Świcie masz wszystkie 3 Kamienie.
        """);
    }

    private static void Report(ActionResult result)
    {
        if (!result.Ok) Error(result.Error ?? "Nie można tego zrobić.");
    }

    private static void Error(string message) => Console.WriteLine("  ! " + message);

    private static string Pad(string text, int width) =>
        text.Length > width ? text[..width] : text.PadRight(width);

    private static string TypeName(CardType type) => type switch
    {
        CardType.Unit => "Jednostka",
        CardType.Spell => "Zaklęcie",
        CardType.Artifact => "Artefakt",
        CardType.Land => "Kraina",
        _ => "Bohater",
    };

    internal static string ReasonName(WinReason? reason) => reason switch
    {
        WinReason.Demolition => "Zburzenie Twierdzy",
        WinReason.Dominance => "Dominacja (3 Kamienie Mocy)",
        WinReason.TurnLimit => "limit 20 tur",
        _ => "nieznany",
    };
}

internal static class Simulation
{
    public static void Run(CardLibrary lib, int games)
    {
        const string a = "zakon-plomienia";
        const string b = "lesny-krag";
        int winsA = 0, winsB = 0, draws = 0, firstWins = 0, decided = 0;
        long totalTurns = 0;
        var reasons = new Dictionary<WinReason, int>();

        for (var i = 0; i < games; i++)
        {
            var game = Game.Create(lib, a, b, seed: i);
            game.Begin();
            for (var guard = 0; !game.State.IsOver && guard < 200; guard++)
                SimpleBot.PlayTurn(game);

            var s = game.State;
            totalTurns += s.Players[0].TurnsTaken + s.Players[1].TurnsTaken;
            if (s.WinReason is { } r) reasons[r] = reasons.GetValueOrDefault(r) + 1;
            if (s.IsDraw || s.Winner is null) { draws++; continue; }
            decided++;
            if (s.Winner == 0) winsA++; else winsB++;
            if (s.Winner == s.FirstPlayer) firstWins++;
        }

        string Pct(int x, int of) => of == 0 ? "-" : $"{100.0 * x / of:0.0}%";
        Console.WriteLine($"Symulacja: {games} partii bot kontra bot");
        Console.WriteLine($"  Zakon Płomienia wygrywa: {winsA} ({Pct(winsA, games)})");
        Console.WriteLine($"  Leśny Krąg wygrywa:      {winsB} ({Pct(winsB, games)})");
        Console.WriteLine($"  Remisy:                  {draws} ({Pct(draws, games)})");
        Console.WriteLine($"  Zaczynający wygrywa:     {Pct(firstWins, decided)} rozstrzygniętych partii");
        Console.WriteLine($"  Średnio tur na gracza:   {totalTurns / 2.0 / Math.Max(1, games):0.0}");
        foreach (var (reason, count) in reasons.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {Session.ReasonName(reason)}: {count} ({Pct(count, games)})");
    }
}
