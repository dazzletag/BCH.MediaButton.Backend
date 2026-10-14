namespace MediaButtonBackend.Services;

/// <summary>
/// Turns a resident's "This is Me" record into a prompt for their own
/// television channel ident.
///
/// The screen says "Darren Taggart TV — curated by us just for you", and the
/// picture behind it should look like a channel of one's own: bright, cheerful
/// and obviously made for this person. Not a tasteful landscape — a title card.
///
/// Three judgements, each deliberate:
///
/// <para><b>Motifs, not a scene.</b> Two or three objects a person liked, set
/// out like channel branding. A landscape would be quieter but says nothing
/// about whose channel it is; a guitar, a vinyl record and a happy dog say it
/// at a glance.</para>
///
/// <para><b>Things, not people.</b> A favourite band cannot be drawn without
/// faces, and a generated face can be taken for a relative who is not there.
/// So music becomes a guitar and a record, football becomes a ball and a
/// rosette. The interest survives; the people do not.</para>
///
/// <para><b>Room for the name.</b> The name and strapline are drawn afterwards
/// in PIL, because image models still cannot spell reliably. The prompt asks
/// for the bottom third to be left plain: a hole in the middle is something
/// these models reliably ignore, and the first real attempt put the dog
/// squarely behind the lettering. Empty space at an edge they do honour.</para>
/// </summary>
public static class WelcomeImagePrompt
{
    /// <summary>Fields in a This is Me record worth drawing from, best first.</summary>
    private static readonly string[] SubjectFieldHints =
    [
        "hobbies", "hobby", "interests", "interest", "likes", "enjoys", "pastimes",
        "music", "favourite music", "songs",
        "occupation", "job", "work", "career", "profession",
        "places", "place", "holidays", "holiday", "birthplace", "born",
        "pets", "pet", "animals", "sport", "sports", "team",
    ];

    /// <summary>
    /// What a liking looks like as an object. Almost anything can be drawn
    /// once it is a thing rather than a person — which is what makes a channel
    /// ident a better fit here than a landscape.
    /// </summary>
    private static readonly (string[] Match, string Motif)[] Motifs =
    [
        (["beatles", "rock", "pop", "guitar", "band", "music", "song", "singer",
          "album", "record", "concert", "elvis", "abba", "sinatra", "morissette",
          "alanis", "jazz", "blues", "choir", "hymn", "orchestra", "brass"],
         "a classic electric guitar and a stack of vinyl records"),
        (["piano", "organ", "accordion"], "an upright piano with sheet music"),
        (["dog", "puppy", "spaniel", "labrador", "terrier", "collie"],
         "a cheerful dog sitting with its tongue out"),
        (["cat", "kitten"], "a contented cat curled up"),
        (["horse", "pony", "riding"], "a friendly horse looking over a stable door"),
        (["bird", "robin", "budgie", "pigeon"], "a bright little robin on a branch"),
        (["garden", "gardening", "flower", "rose", "allotment", "plant"],
         "a watering can spilling bright flowers"),
        (["sea", "beach", "coast", "seaside", "pier"],
         "a striped deckchair and a bucket and spade"),
        (["boat", "sail", "canal", "barge", "river", "lake"], "a little painted boat"),
        (["fish", "angling"], "a fishing rod and a wicker creel"),
        (["train", "railway", "steam", "locomotive"], "a jolly steam engine"),
        (["car", "motor", "vintage", "motorbike"], "a bright classic car"),
        (["walk", "hiking", "rambl", "countryside", "hill", "moor"],
         "a pair of walking boots and a flask"),
        (["farm", "tractor", "sheep", "cattle", "cow"], "a red tractor"),
        (["cook", "baking", "bake", "cake", "kitchen"], "a frosted cake and a teapot"),
        (["tea", "coffee"], "a steaming teacup and saucer"),
        (["knit", "sew", "needle", "craft", "quilt"], "balls of wool and knitting needles"),
        (["football", "soccer"], "a football and a club rosette"),
        (["cricket"], "a cricket bat and ball"),
        (["bowls", "golf", "tennis", "darts"], "sports equipment arranged neatly"),
        (["book", "reading", "library", "poetry"], "a stack of well-read books"),
        (["film", "movie", "cinema", "television", "soap", "programme"],
         "an old television set and a film reel"),
        (["dance", "dancing", "ballroom"], "a pair of dancing shoes and a mirror ball"),
        (["church", "chapel", "faith", "bible"], "a country church with a bright blue sky"),
        (["pub", "beer", "ale"], "a frothy pint glass on a beer mat"),
        (["bingo", "cards", "crossword", "puzzle", "quiz"], "playing cards and a crossword"),
        (["travel", "holiday", "abroad", "spain", "cornwall", "wales", "scotland"],
         "a suitcase covered in travel labels"),
        (["wood", "forest", "tree", "park", "nature"], "a leafy green tree"),
        (["army", "navy", "raf", "forces", "military"], "a row of polished medals"),
        (["nurse", "nursing", "teacher", "teaching", "school"],
         "a satchel and a vase of flowers"),
    ];

    /// <summary>
    /// Phrases that must never reach an image model. A This is Me record is a
    /// care document: it holds illnesses, bereavements, distress triggers and
    /// safeguarding notes. None of that belongs in a picture, and some of it
    /// would be actively harmful to render.
    /// </summary>
    private static readonly string[] Excluded =
    [
        "dementia", "alzheimer", "diagnos", "medication", "medicine", "allergy",
        "allerg", "risk", "safeguard", "behaviour", "aggress", "distress",
        "anxiet", "depress", "pain", "fall", "falls", "continence", "catheter",
        "weight", "diet", "swallow", "dnacpr", "resuscitat", "end of life",
        "palliative", "deceased", "died", "death", "widow", "bereave", "funeral",
        "next of kin", "nhs", "gp", "hospital", "ward", "admission",
    ];

    public record Result(string Prompt, string? Subject, string Basis);

    /// <summary>
    /// Build the prompt. <paramref name="formFields"/> is the This is Me record.
    /// With nothing personal recorded it still produces a cheerful ident, rather
    /// than inventing a life the resident did not have.
    /// </summary>
    public static Result Build(string residentName, IReadOnlyDictionary<string, string>? formFields)
    {
        var found = PickMotifs(formFields);

        if (found.Count == 0)
        {
            return new Result(
                Ident("a television set, a teacup and a vase of cheerful flowers"),
                null,
                "Nothing personal was recorded, so a general cheerful ident was used. "
                + "Adding interests to this resident's This is Me record — a pet, their "
                + "music, a hobby — would put those things on their channel.");
        }

        var motifs = string.Join(", ", found.Select(f => f.Motif));
        var interests = string.Join(", ", found.Select(f => f.Interest));

        return new Result(
            Ident(motifs),
            interests,
            $"Built from their recorded interests: {interests}.");
    }

    /// <summary>The channel-ident instruction wrapped around the motifs.</summary>
    private static string Ident(string motifs) =>
        "A bright, friendly television channel ident, like the opening title card of a "
        + "cheerful daytime programme. Featuring " + motifs + ", "
        + "drawn as bold, charming, colourful illustration with clean shapes, warm sunny "
        + "colours and a soft gradient background. Playful and uplifting, with a retro "
        + "television feel. Arrange the objects across the upper two thirds of the "
        + "picture, and leave the bottom third plain and empty — just background "
        + "colour or a simple gradient there, with nothing in it. "
        + "Landscape format, 16:9. "
        // Negatives last, plainly worded. These models follow the tail of a
        // prompt closely, and each of these has a reason.
        + "No people, no faces, no figures, no hands. "
        + "No text, letters, words, numbers, captions, logos or signatures. "
        + "Nothing frightening, gloomy, surreal or distorted.";

    /// <summary>
    /// Up to three interests, as objects. More than three and an ident becomes
    /// a jumble; fewer and it says little about whose channel it is.
    /// </summary>
    private static List<(string Interest, string Motif)> PickMotifs(
        IReadOnlyDictionary<string, string>? fields)
    {
        var chosen = new List<(string Interest, string Motif)>();
        if (fields is null || fields.Count == 0)
            return chosen;

        var usedMotifs = new HashSet<string>();

        foreach (var hint in SubjectFieldHints)
        {
            foreach (var (key, raw) in fields)
            {
                if (key is null || raw is null)
                    continue;
                if (!key.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var item in Items(raw))
                {
                    var match = Motifs.FirstOrDefault(
                        m => m.Match.Any(w => item.Contains(w, StringComparison.OrdinalIgnoreCase)));
                    if (match.Motif is null)
                        continue;
                    // Two bands are still one guitar: avoid drawing it twice.
                    if (!usedMotifs.Add(match.Motif))
                        continue;

                    chosen.Add((item, match.Motif));
                    if (chosen.Count == 3)
                        return chosen;
                }
            }
        }
        return chosen;
    }

    /// <summary>
    /// Each separately-listed interest in a field, cleaned.
    ///
    /// "and" counts as a separator, not just punctuation. People write "the
    /// beatles, alanis morissette and dogs", and splitting on commas alone
    /// buries the dog inside the singer — where the music motif claims the
    /// whole item and the dog is never seen.
    /// </summary>
    private static IEnumerable<string> Items(string raw)
    {
        var separated = System.Text.RegularExpressions.Regex.Replace(
            raw, @"\s+and\s+", ",", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var part in separated.Split(',', ';', '\n', '\r', '/', '|', '&'))
        {
            var cleaned = Clean(part);
            if (cleaned is not null)
                yield return cleaned;
        }
    }

    /// <summary>Reduce one listed item to a short, safe phrase, or null.</summary>
    private static string? Clean(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
            return null;

        // Care records use these for "nothing recorded".
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("na", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("not known", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("not applicable", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var lead in new[] { "likes ", "loves ", "enjoys ", "and ", "the " })
        {
            if (text.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                text = text[lead.Length..].Trim();
        }

        if (text.Length <= 2)
            return null;

        if (Excluded.Any(bad => text.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            return null;

        // Long free text is a nurse's paragraph, not an interest.
        return text.Length > 70 ? text[..70].TrimEnd() : text;
    }
}
