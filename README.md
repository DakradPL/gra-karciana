# Rozdarte Krainy

Mobilna kolekcjonerska gra karciana (TCG) w świecie fantasy. Ten katalog zawiera silnik zasad gry.

## Struktura

| Ścieżka | Co zawiera |
| --- | --- |
| `src/RozdarteKrainy.Engine` | silnik zasad w C# (.NET 8), bez zależności od Unity, z prostym botem |
| `src/RozdarteKrainy.Cli` | gra w terminalu przeciw botowi i symulacja bot kontra bot |
| `src/RozdarteKrainy.Engine/Data/cards.json` | wszystkie karty z prototypu (Zakon Płomienia, Leśny Krąg) |
| `src/RozdarteKrainy.Engine/Data/decks.json` | dwie talie startowe po 30 kart |
| `tests/RozdarteKrainy.Engine.Tests` | testy zasad (xUnit) |
| `.github/workflows/ci.yml` | automatyczne testy na GitHubie przy każdym pushu |

## Zagraj w terminalu

Wymaga [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run --project src/RozdarteKrainy.Cli
```

Wybierasz talię, a bot dostaje drugą. Najważniejsze komendy (pełna lista pod `?` w grze):

| Komenda | Co robi |
| --- | --- |
| `z 0 LF` | zagraj kartę 0 z ręki na lewy Front (L/S/P = ścieżka, F/T = Front/Tyły) |
| `z 2 !SF` | zaklęcie na jednostkę rywala na środkowym Froncie |
| `r LF SF` | przesuń jednostkę (1 Esencja) |
| `m !tw` | moc Bohatera w Twierdzę rywala |
| `k` | koniec tury |

## Symulacja balansu

```
dotnet run --project src/RozdarteKrainy.Cli -- --symulacja 500
```

Rozgrywa 500 partii bot kontra bot i podaje procent zwycięstw talii, przewagę zaczynającego,
długość partii i sposoby zwycięstwa.

## Uruchomienie testów

```
dotnet test tests/RozdarteKrainy.Engine.Tests/RozdarteKrainy.Engine.Tests.csproj
```

## Jak używać silnika

```csharp
var lib = CardLibrary.LoadDefault();
var game = Game.Create(lib, "zakon-plomienia", "lesny-krag", seed: 123);
game.Mulligan(0, new[] { 2 });   // opcjonalnie
game.Begin();                    // Świt pierwszej tury
game.PlayCard(0, new Position(1, Row.Front));
game.EndTurn();                  // Starcie, Zmierzch, tura rywala
```

Każda akcja zwraca `ActionResult` (`Ok` albo `Error` z opisem po polsku). Przy tym samym ziarnie
partia przebiega identycznie, więc serwer może sprawdzać ruchy klienta.

## Zaimplementowane zasady

- Plansza 3 ścieżki × Front/Tyły, Twierdza 25 PŻ, Kamienie Mocy.
- Esencja: 1 na turę więcej do 7, +1 za Kamień, Zapas 1, Iskra drugiego gracza.
- Tura: Świt, Działania (karty, ruch za 1 Esencję, moc Bohatera), Starcie, Zmierzch.
- Zwycięstwo: Zburzenie, Dominacja, Wyczerpanie, limit 20 tur.
- Słowa kluczowe: Szarża, Straż, Zasięg, Ukrycie, Przebicie, Strażnik Kamienia, Więź, Echo.
- Typy kart: Jednostka, Zaklęcie, Artefakt, Kraina, Bohater z mocą i Przebudzeniem.
- Walidacja talii.

Zmiana względem dokumentu: Kamień przejmuje gracz, który ma jednostkę na **Froncie** ścieżki,
gdy rywal nie ma jednostki na Froncie tej ścieżki. Inaczej Strażnik Kamienia nic by nie robił.
