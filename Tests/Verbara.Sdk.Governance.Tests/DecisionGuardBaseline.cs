using System.Text;
using System.Text.Json;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// One place a decision guard fires. <see cref="Path"/> is repo-relative with forward slashes,
/// <see cref="Key"/> is the text of the offending site, and <see cref="Line"/> (1-based) is for the
/// failure message only — it is never matched, so an unrelated edit above a site does not churn the
/// baseline.
/// </summary>
internal sealed record DecisionGuardSite(string Path, int Line, string Key);

/// <summary>
/// One known site listed in <c>decision-guards-baseline.json</c>: the guard it belongs to, the
/// repo-relative file, the normalized key of the site, why it is there and the change that owns its
/// fix.
/// </summary>
internal sealed record DecisionGuardBaselineEntry(string Guard, string Path, string Key, string Reason, string Owner);

/// <summary>
/// The outcome of matching one guard's firing sites against the baseline. The match is exact only
/// when both lists are empty: <see cref="Unlisted"/> holds sites that fire and are not listed,
/// <see cref="Stale"/> holds entries whose site no longer fires.
/// </summary>
internal sealed record DecisionGuardMatch(
    string Guard,
    IReadOnlyList<DecisionGuardSite> Unlisted,
    IReadOnlyList<DecisionGuardBaselineEntry> Stale)
{
    public bool IsExact => Unlisted.Count == 0 && Stale.Count == 0;

    /// <summary>The failure message a guard hands to its assertion; empty when the match is exact.</summary>
    public string Describe()
    {
        if (IsExact)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("Guard '").Append(Guard).Append("' does not match ")
            .Append(DecisionGuardBaseline.FileName).AppendLine(" exactly.");

        if (Unlisted.Count > 0)
        {
            sb.Append(Unlisted.Count).AppendLine(
                " site(s) fire that the baseline does not list. Fix the site: the baseline holds only " +
                "defects that were in the tree when their guard landed, each naming the change that owns its fix.");
            foreach (var site in Unlisted)
            {
                sb.Append("  ").Append(site.Path).Append(':').Append(site.Line)
                    .Append("  key: ").AppendLine(DecisionGuardBaseline.DisplayKey(site.Key));
            }
        }

        if (Stale.Count > 0)
        {
            sb.Append(Stale.Count).AppendLine(
                " baseline entries are stale: their site no longer fires. The change that fixed a site " +
                "must delete its entry in the same commit, so the baseline only shrinks:");
            foreach (var entry in Stale)
            {
                sb.Append("  ").Append(entry.Path)
                    .Append("  key: ").Append(DecisionGuardBaseline.DisplayKey(entry.Key))
                    .Append("  (owner: ").Append(entry.Owner).AppendLine(")");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Reader for <c>decision-guards-baseline.json</c>, the one shared baseline of the decision guards
/// that fire on a known defect when they land (openspec change
/// <c>a-decision-is-held-by-a-test-that-can-fail</c>, design D1).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>sync-fence-baseline.json</c>, which tolerates any count up to a ceiling, this baseline is
/// an <b>exact</b> match on <c>{guard, path, key}</c>. A guard fails on a site the baseline does not
/// list, <b>and</b> on an entry whose site no longer fires — so the change that fixes a site cannot
/// leave its entry behind to excuse a later regression at the same place. A count per file was
/// rejected because it cannot tell a fixed site from a moved one; inline allow-markers were rejected
/// because they would edit the very files whose fixes the entries are waiting for.
/// </para>
/// <para>
/// Matching counts occurrences: two textually identical sites in one file need two entries, and one
/// entry excuses one site. Two of the sites known when this reader landed are exactly that shape
/// (<c>catch (IOException) { }</c> twice in one file), and a set that collapsed them would let a
/// third copy in unnoticed.
/// </para>
/// <para>
/// The key is compared in the form <see cref="NormalizeKey"/> gives it, which drops whitespace that
/// does not separate two tokens, so re-indenting or wrapping a site does not unlist it. The line
/// number is never compared.
/// </para>
/// <para>
/// Every guard that reads this file must be named in <see cref="RegisteredGuards"/>: an entry whose
/// guard no test matches would never be found stale, which is the failure this file exists to stop.
/// </para>
/// </remarks>
internal sealed class DecisionGuardBaseline
{
    public const string FileName = "decision-guards-baseline.json";

    /// <summary>
    /// The guards that match their sites against this baseline. A guard adds its name here when it
    /// lands; <see cref="EntriesOfUnregisteredGuards"/> reports any entry naming a guard not listed.
    /// </summary>
    public static readonly IReadOnlySet<string> RegisteredGuards = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskRunHandoffScanner.GuardName,
        EmptyCatchScanner.GuardName,
    };

    private static readonly string[] EntryFields = ["guard", "path", "key", "reason", "owner"];

    private DecisionGuardBaseline(IReadOnlyList<DecisionGuardBaselineEntry> entries) => Entries = entries;

    /// <summary>Every entry, in file order.</summary>
    public IReadOnlyList<DecisionGuardBaselineEntry> Entries { get; }

    /// <summary>
    /// Loads the committed baseline from the repo root. A missing file throws rather than reading as
    /// empty: the file is committed, so its absence means the locator broke, and an empty baseline
    /// would pass every guard that happens to fire nowhere.
    /// </summary>
    public static DecisionGuardBaseline LoadCommitted()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        var path = Path.Join(repoRoot, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The decision-guard baseline is missing at '{path}'.", path);

        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses a baseline document. The shape is strict — an object with an <c>entries</c> array (and
    /// an optional <c>_comment</c>), each entry carrying exactly the five string fields, none blank,
    /// with a repo-relative forward-slash path — because a misspelt field would otherwise read as a
    /// missing owner or an entry that silently never matches.
    /// </summary>
    public static DecisionGuardBaseline Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{FileName}: the document must be a JSON object.");

        JsonElement? entriesElement = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "_comment":
                    break;
                case "entries":
                    entriesElement = property.Value;
                    break;
                default:
                    throw new InvalidDataException(
                        $"{FileName}: unknown top-level property '{property.Name}'; expected 'entries' and an optional '_comment'.");
            }
        }

        if (entriesElement is not { ValueKind: JsonValueKind.Array } entriesArray)
            throw new InvalidDataException($"{FileName}: the document must carry an 'entries' array.");

        var entries = new List<DecisionGuardBaselineEntry>();
        var index = 0;
        foreach (var element in entriesArray.EnumerateArray())
        {
            entries.Add(ParseEntry(element, index));
            index++;
        }

        return new DecisionGuardBaseline(entries);
    }

    /// <summary>
    /// The form a key is compared in: every run of whitespace (spaces, tabs, line breaks) is dropped,
    /// except between two identifier characters, where it separates two tokens and becomes one
    /// space. <c>catch (IOException)\n{\n}</c> and <c>catch(IOException){}</c> are the same key, so a
    /// site survives re-indentation and line wrapping; <c>return x</c> and <c>returnx</c> are not.
    /// </summary>
    public static string NormalizeKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sb = new StringBuilder(text.Length);
        var sawWhitespace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                sawWhitespace = true;
                continue;
            }

            if (sawWhitespace && sb.Length > 0 && IsIdentifierChar(sb[^1]) && IsIdentifierChar(c))
                sb.Append(' ');
            sawWhitespace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The form a key is shown in: whitespace runs collapsed to one space, so a failure message
    /// quotes the site as a reader wrote it and it can be pasted into an entry unchanged.
    /// </summary>
    public static string DisplayKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Matches the sites one guard found against that guard's entries. Entries of other guards are
    /// neither consumed nor reported. Each entry excuses at most one site with the same path and
    /// normalized key; a site left over is unlisted, an entry left over is stale.
    /// </summary>
    public DecisionGuardMatch Match(string guard, IEnumerable<DecisionGuardSite> sites)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guard);
        ArgumentNullException.ThrowIfNull(sites);

        // Entries are tracked by their position, not by value: two identical entries are two
        // allowances, and a record's value equality would merge them.
        var remaining = new Dictionary<(string Path, string Key), Queue<int>>();
        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            if (!string.Equals(entry.Guard, guard, StringComparison.Ordinal))
                continue;

            var slot = (entry.Path, NormalizeKey(entry.Key));
            if (!remaining.TryGetValue(slot, out var queue))
            {
                queue = new Queue<int>();
                remaining[slot] = queue;
            }

            queue.Enqueue(i);
        }

        var unlisted = new List<DecisionGuardSite>();
        var ordered = sites
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ThenBy(s => s.Line);
        foreach (var site in ordered)
        {
            if (remaining.TryGetValue((site.Path, NormalizeKey(site.Key)), out var queue) && queue.Count > 0)
                queue.Dequeue();
            else
                unlisted.Add(site);
        }

        var stale = remaining.Values
            .SelectMany(q => q)
            .Order()
            .Select(i => Entries[i])
            .ToList();

        return new DecisionGuardMatch(guard, unlisted, stale);
    }

    /// <summary>
    /// Entries naming a guard outside <paramref name="registered"/>. No guard test would ever match
    /// them, so they could never be reported stale; they are reported here instead.
    /// </summary>
    public IReadOnlyList<DecisionGuardBaselineEntry> EntriesOfUnregisteredGuards(IReadOnlySet<string> registered)
    {
        ArgumentNullException.ThrowIfNull(registered);

        return Entries.Where(e => !registered.Contains(e.Guard)).ToList();
    }

    private static DecisionGuardBaselineEntry ParseEntry(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{FileName}: entries[{index}] must be a JSON object.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!EntryFields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{FileName}: entries[{index}] has unknown property '{property.Name}'; " +
                    $"an entry carries exactly {string.Join(", ", EntryFields)}.");
            }

            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw new InvalidDataException($"{FileName}: entries[{index}].{property.Name} must be a non-blank string.");

            values[property.Name] = property.Value.GetString()!;
        }

        foreach (var field in EntryFields)
        {
            if (!values.ContainsKey(field))
                throw new InvalidDataException($"{FileName}: entries[{index}] is missing '{field}'.");
        }

        var path = values["path"];
        if (path.Contains('\\', StringComparison.Ordinal) || path.StartsWith('/') || path.StartsWith("./", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{FileName}: entries[{index}].path '{path}' must be repo-relative with forward slashes.");
        }

        return new DecisionGuardBaselineEntry(values["guard"], path, values["key"], values["reason"], values["owner"]);
    }
}
