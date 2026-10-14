namespace MediaButtonBackend.Services;

/// <summary>
/// Turns a resident's "This is Me" record into a prompt for one picture.
///
/// This is the screen a resident sees before anything plays, so the picture is
/// not decoration — it is the first thing that tells them the television is
/// theirs. Three judgements are built in, and each is deliberate:
///
/// <para><b>One subject, not a collage.</b> A resident who liked gardening, brass
/// bands and Blackpool gets a garden, or a bandstand, or the promenade — never
/// all three in one image. Asking a model for several unrelated things at once
/// is what produces the cluttered, dreamlike results that read as artificial.</para>
///
/// <para><b>Somewhere real, not an abstract.</b> The existing prompt asked for
/// "a warm, cheerful abstract background scene". Abstract and surreal imagery is
/// a poor fit for people living with dementia, who may try to make literal sense
/// of what they see; a recognisable place is kinder and more likely to be
/// recognised with pleasure.</para>
///
/// <para><b>Nobody in it.</b> No people, no faces. A generated face can be taken
/// for a relative who is not there, and that is worse than no picture at all.</para>
/// </summary>
public static class WelcomeImagePrompt
{
    /// <summary>Fields in a This is Me record worth drawing a picture from, best first.</summary>
    private static readonly string[] SubjectFieldHints =
    [
        "hobbies", "hobby", "interests", "interest", "likes", "enjoys", "pastimes",
        "occupation", "job", "work", "career", "profession",
        "music", "favourite music", "songs",
        "places", "place", "holidays", "holiday", "birthplace", "born",
        "pets", "pet", "animals", "sport", "sports", "team",
    ];

    /// <summary>
    /// Phrases that must never reach an image model. A This is Me record is a
    /// care document: it contains illnesses, bereavements, distress triggers and
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
    /// Returns a gentle generic scene when there is nothing personal to draw on,
    /// rather than inventing a life the resident did not have.
    /// </summary>
    public static Result Build(string residentName, IReadOnlyDictionary<string, string>? formFields)
    {
        var subject = PickSubject(formFields);

        if (subject is null)
        {
            // No usable record. Somewhere pleasant and unremarkable is the honest
            // answer — better than guessing at a past from a name or a date.
            return new Result(
                Scene("a quiet English country garden in late summer, with a wooden bench, "
                      + "hollyhocks and a brick wall"),
                null,
                "No usable interests recorded, so a general scene was used. Adding to this "
                + "resident's This is Me record would make the picture personal to them.");
        }

        return new Result(
            Scene(subject),
            subject,
            $"Drawn from this resident's recorded interests: “{subject}”.");
    }

    /// <summary>The instruction wrapped around whatever the subject turns out to be.</summary>
    private static string Scene(string subject) =>
        "A single peaceful scene: " + subject + ". "
        + "Painted in a warm, realistic, representational style, like a good oil painting "
        + "or a bright photograph on a clear day. Soft natural daylight, gentle colours, "
        + "calm and uncluttered, one clear subject with plenty of open space. "
        + "Landscape format, 16:9. "
        // Negatives last and plainly worded: these models follow the tail of a
        // prompt closely, and every one of these has a reason.
        + "No people, no faces, no figures, no animals in distress. "
        + "No text, letters, words, numbers, captions or signatures. "
        + "Nothing surreal, abstract, distorted, dreamlike or frightening. "
        + "Nothing dark, gloomy, cluttered or crowded. Not a collage.";

    private static string? PickSubject(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null || fields.Count == 0)
            return null;

        foreach (var hint in SubjectFieldHints)
        {
            foreach (var (key, raw) in fields)
            {
                if (key is null || raw is null)
                    continue;
                if (!key.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Clean(raw);
                if (value is not null)
                    return value;
            }
        }
        return null;
    }

    /// <summary>
    /// Reduce a field to one short, safe phrase, or null if nothing survives.
    /// Takes the first item of a list rather than joining them, because one
    /// subject is the whole point.
    /// </summary>
    private static string? Clean(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
            return null;

        // Care records use these for "nothing recorded"; a picture of "N/A" is
        // not what anybody wants.
        if (text.Length <= 4 ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("na", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("not known", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("not applicable", StringComparison.OrdinalIgnoreCase))
            return null;

        var first = text.Split(',', ';', '\n', '\r', '/', '|')
                        .Select(p => p.Trim())
                        .FirstOrDefault(p => p.Length > 4);
        if (first is null)
            return null;

        if (Excluded.Any(bad => first.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            return null;

        // Long free text is a nurse's paragraph, not a subject for a painting.
        if (first.Length > 90)
            first = first[..90].TrimEnd();

        return first;
    }
}
