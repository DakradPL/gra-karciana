using System.Text.Json;
using System.Text.Json.Serialization;

namespace RozdarteKrainy.Engine;

public sealed class DeckList
{
    public string Name { get; init; } = "";
    public string Hero { get; init; } = "";
    public Dictionary<string, int> Cards { get; init; } = new();
}

/// <summary>Karty i talie wczytywane z plików JSON (Data/cards.json, Data/decks.json).</summary>
public sealed class CardLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, CardDefinition> _cards;

    public IReadOnlyDictionary<string, CardDefinition> Cards => _cards;
    public IReadOnlyDictionary<string, DeckList> Decks { get; }

    public CardLibrary(IEnumerable<CardDefinition> cards, IReadOnlyDictionary<string, DeckList> decks)
    {
        _cards = new Dictionary<string, CardDefinition>();
        foreach (var card in cards)
        {
            if (!_cards.TryAdd(card.Id, card))
                throw new InvalidOperationException($"Powtórzone id karty: {card.Id}");
        }
        Decks = decks;
    }

    public static CardLibrary FromJson(string cardsJson, string decksJson)
    {
        var cards = JsonSerializer.Deserialize<List<CardDefinition>>(cardsJson, JsonOptions)
                    ?? throw new InvalidOperationException("Brak kart w cards.json");
        var decks = JsonSerializer.Deserialize<Dictionary<string, DeckList>>(decksJson, JsonOptions)
                    ?? throw new InvalidOperationException("Brak talii w decks.json");
        return new CardLibrary(cards, decks);
    }

    public static CardLibrary LoadDefault()
    {
        return FromJson(ReadResource("cards.json"), ReadResource("decks.json"));
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(CardLibrary).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Brak zasobu {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public CardDefinition Get(string id) =>
        _cards.TryGetValue(id, out var card) ? card : throw new KeyNotFoundException($"Nieznana karta: {id}");

    public (CardDefinition Hero, List<CardDefinition> Cards) BuildDeck(string deckId)
    {
        if (!Decks.TryGetValue(deckId, out var list))
            throw new KeyNotFoundException($"Nieznana talia: {deckId}");
        var cards = new List<CardDefinition>();
        foreach (var (id, count) in list.Cards)
            for (var i = 0; i < count; i++)
                cards.Add(Get(id));
        return (Get(list.Hero), cards);
    }
}

/// <summary>Zasady budowy talii z dokumentu projektowego.</summary>
public static class DeckValidator
{
    public static List<string> Validate(CardDefinition hero, IReadOnlyList<CardDefinition> cards)
    {
        var errors = new List<string>();
        if (hero.Type != CardType.Hero)
            errors.Add($"{hero.Name} nie jest Bohaterem.");
        if (cards.Count != Rules.DeckSize)
            errors.Add($"Talia ma {cards.Count} kart, a powinna mieć {Rules.DeckSize}.");

        foreach (var group in cards.GroupBy(c => c.Id))
        {
            var card = group.First();
            var limit = card.Rarity == Rarity.Legendary ? Rules.MaxLegendaryCopies : Rules.MaxCopies;
            if (group.Count() > limit)
                errors.Add($"{card.Name}: {group.Count()} kopie, limit to {limit}.");
            if (card.Type == CardType.Hero || card.IsToken)
                errors.Add($"{card.Name} nie może być w talii.");
        }

        var factions = cards.Where(c => c.Faction != Faction.Neutral && c.Faction != hero.Faction)
                            .GroupBy(c => c.Faction).ToList();
        if (factions.Count > 1)
            errors.Add("Talia może mieć najwyżej jedną frakcję pomocniczą.");
        else if (factions.Count == 1 && factions[0].Count() > Rules.MaxSecondaryFactionCards)
            errors.Add($"Z frakcji pomocniczej może być najwyżej {Rules.MaxSecondaryFactionCards} kart.");

        if (cards.Count(c => c.Type == CardType.Land) > Rules.MaxLands)
            errors.Add($"Najwyżej {Rules.MaxLands} Krainy w talii.");

        return errors;
    }
}
