using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace GitHr.Core.Diff;

/// <summary>A colored range of a diff line's content (see <see cref="TextRange"/>). Color is "#RRGGBB".</summary>
public readonly record struct SyntaxSpan(int Start, int Length, string Color, bool Bold = false, bool Italic = false);

/// <summary>
/// Syntax highlighting for diffs with TextMate grammars (the ones VS Code uses) and the Dark+ theme.
/// The old side (context + removed lines) and the new side (context + added lines) are tokenized as two separate
/// streams, so multi-line constructs like block comments are colored correctly on each side.
/// </summary>
public sealed class SyntaxHighlighter
{
    /// <summary>Beyond this, highlighting is skipped: the diff stays fast, just uncolored.</summary>
    public const int MaxLines = 20_000;

    public const int MaxLineLength = 2_000;

    /// <summary>
    /// Generous per line: the first lines of a file type also compile the grammar's regexes (tens of milliseconds),
    /// and a line that hits the limit loses the rest of its colors. <see cref="Budget"/> bounds the whole diff.
    /// </summary>
    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>Total time per diff; the lines after it stay uncolored.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>Extensions the bundled grammars don't know by name, mapped to one they do.</summary>
    private static readonly Dictionary<string, string> ExtensionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".axaml"] = ".xml", [".xaml"] = ".xml", [".csproj"] = ".xml", [".vbproj"] = ".xml", [".fsproj"] = ".xml",
        [".props"] = ".xml", [".targets"] = ".xml", [".slnx"] = ".xml", [".resx"] = ".xml", [".nuspec"] = ".xml",
        [".config"] = ".xml", [".manifest"] = ".xml", [".svg"] = ".xml",
    };

    private static readonly Lazy<SyntaxHighlighter> SharedInstance = new(() => new SyntaxHighlighter());

    private readonly Lock _gate = new();
    private readonly RegistryOptions _options;
    private readonly Registry _registry;
    private readonly Theme _theme;
    private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);

    public SyntaxHighlighter()
    {
        _options = new RegistryOptions(ThemeName.DarkPlus);
        _registry = new Registry(_options);
        _theme = _registry.GetTheme();
    }

    /// <summary>Loading grammars is expensive; the app shares one instance.</summary>
    public static SyntaxHighlighter Shared => SharedInstance.Value;

    /// <summary>True when <paramref name="path"/>'s file type has a grammar.</summary>
    public bool Supports(string path) => GetGrammar(path) is not null;

    /// <summary>Colored ranges per diff line index. Lines that are plain text (or not code) are left out.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>> Highlight(string path, IReadOnlyList<DiffLine> lines)
    {
        var result = new Dictionary<int, IReadOnlyList<SyntaxSpan>>();
        if (lines.Count > MaxLines)
        {
            return result;
        }

        lock (_gate)
        {
            if (GetGrammar(path) is not { } grammar)
            {
                return result;
            }

            IStateStack? oldState = null;
            IStateStack? newState = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < lines.Count && elapsed.Elapsed < Budget; i++)
            {
                var line = lines[i];
                switch (line.Kind)
                {
                    case DiffLineKind.Hunk:
                        // Hunks are separated by unknown lines: start fresh so one unclosed string can't color the rest.
                        oldState = newState = null;
                        break;
                    case DiffLineKind.Removed:
                        oldState = Tokenize(grammar, InlineChanges.Content(line), oldState, i, result);
                        break;
                    case DiffLineKind.Added:
                        newState = Tokenize(grammar, InlineChanges.Content(line), newState, i, result);
                        break;
                    case DiffLineKind.Context:
                        // Both sides have this line; tokenize it once, continuing the new side.
                        newState = oldState = Tokenize(grammar, InlineChanges.Content(line), newState, i, result);
                        break;
                }
            }
        }
        return result;
    }

    /// <summary>Colored ranges per line of a whole file (e.g. for blame), tokenized as one stream.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>> Highlight(string path, IReadOnlyList<string> lines)
    {
        var result = new Dictionary<int, IReadOnlyList<SyntaxSpan>>();
        if (lines.Count > MaxLines)
        {
            return result;
        }

        lock (_gate)
        {
            if (GetGrammar(path) is not { } grammar)
            {
                return result;
            }

            IStateStack? state = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < lines.Count && elapsed.Elapsed < Budget; i++)
            {
                state = Tokenize(grammar, lines[i], state, i, result);
            }
        }
        return result;
    }

    private IStateStack? Tokenize(IGrammar grammar, string text, IStateStack? state, int index, Dictionary<int, IReadOnlyList<SyntaxSpan>> result)
    {
        if (text.Length > MaxLineLength)
        {
            return null;
        }

        var tokenized = grammar.TokenizeLine(text, state, LineTimeLimit);
        var spans = new List<SyntaxSpan>();
        foreach (var token in tokenized.Tokens)
        {
            var start = Math.Min(token.StartIndex, text.Length);
            var end = Math.Min(token.EndIndex, text.Length);
            if (end <= start)
            {
                continue;
            }

            string? color = null;
            var fontStyle = TextMateSharp.Themes.FontStyle.NotSet;
            foreach (var rule in _theme.Match(token.Scopes))
            {
                if (color is null && rule.foreground > 0)
                {
                    color = _theme.GetColor(rule.foreground);
                }
                if (fontStyle == TextMateSharp.Themes.FontStyle.NotSet && rule.fontStyle > 0)
                {
                    fontStyle = (TextMateSharp.Themes.FontStyle)rule.fontStyle;
                }
            }
            if (color is not null)
            {
                // NotSet is -1, which has every flag bit set.
                var style = fontStyle == TextMateSharp.Themes.FontStyle.NotSet ? TextMateSharp.Themes.FontStyle.None : fontStyle;
                spans.Add(new SyntaxSpan(start, end - start, color,
                    style.HasFlag(TextMateSharp.Themes.FontStyle.Bold), style.HasFlag(TextMateSharp.Themes.FontStyle.Italic)));
            }
        }
        if (spans.Count > 0)
        {
            result[index] = spans;
        }
        return tokenized.RuleStack;
    }

    private IGrammar? GetGrammar(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }
        extension = ExtensionAliases.GetValueOrDefault(extension, extension);

        lock (_gate)
        {
            if (!_grammars.TryGetValue(extension, out var grammar))
            {
                var language = _options.GetLanguageByExtension(extension);
                grammar = language is null ? null : _registry.LoadGrammar(_options.GetScopeByLanguageId(language.Id));
                _grammars[extension] = grammar;
            }
            return grammar;
        }
    }
}
