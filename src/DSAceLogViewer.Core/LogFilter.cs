using System.Text.RegularExpressions;

namespace DSAceLogViewer.Core;

public sealed partial class LogFilter
{
    private readonly string[] _terms;
    private readonly HashSet<LogCategory>? _categories;
    public bool Inverted { get; }
    public string Query { get; }
    public bool HasSearch => _terms.Length > 0;

    public LogFilter(string? query = null, bool inverted = false, IEnumerable<LogCategory>? categories = null)
    {
        Query = query?.Trim() ?? "";
        _terms = Query.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _categories = categories?.ToHashSet();
        Inverted = inverted;
    }

    public bool Matches(LogEntry entry)
    {
        if (_categories is not null && !_categories.Contains(entry.Category)) return false;
        if (!HasSearch) return true; // An empty inverted query must not hide the entire log.
        var text = entry.Raw.ToUpperInvariant();
        string[]? words = null;
        var matches = _terms.All(term => text.Contains(term, StringComparison.Ordinal)
            || (term.Length >= 3 && CompactSubsequence(term, text))
            || (term.Length >= 4 && (words ??= WordRegex().Matches(text)
                .SelectMany(m => m.Value.Split(['-', '_', '\''], StringSplitOptions.RemoveEmptyEntries).Prepend(m.Value)).ToArray())
                .Any(word => WithinEditDistance(term, word, term.Length >= 8 ? 2 : 1))));
        return Inverted ? !matches : matches;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_'-]+")]
    private static partial Regex WordRegex();

    private static bool CompactSubsequence(string term, string text)
    {
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] != term[0]) continue;
            var matched = 1;
            var previous = start;
            for (var index = start + 1; index < text.Length && index - start < term.Length * 3; index++)
            {
                if (index - previous > 6) break;
                if (text[index] != term[matched]) continue;
                previous = index;
                if (++matched == term.Length) return true;
            }
        }
        return false;
    }

    // Optimal string alignment: substitutions, insertions, omissions and adjacent transpositions.
    private static bool WithinEditDistance(string left, string right, int limit)
    {
        if (Math.Abs(left.Length - right.Length) > limit) return false;
        var previousPrevious = new int[right.Length + 1];
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMinimum = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    current[j] = Math.Min(current[j], previousPrevious[j - 2] + 1);
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }
            if (rowMinimum > limit) return false;
            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }
        return previous[right.Length] <= limit;
    }
}
