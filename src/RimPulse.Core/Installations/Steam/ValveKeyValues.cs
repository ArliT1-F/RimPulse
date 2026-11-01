using System.Text;

namespace RimPulse.Core.Installations.Steam;

/// <summary>Thrown when a Valve KeyValues document (VDF/ACF) is malformed.</summary>
public sealed class ValveKeyValuesFormatException : FormatException
{
    public ValveKeyValuesFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Minimal reader for Valve's KeyValues format, which Steam uses for <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>. RimPulse needs three fields from Steam, so a full third-party VDF library
/// would be an unjustified dependency: quoted strings, nested sections and <c>//</c> comments are enough.
/// </summary>
public sealed class ValveKeyValues
{
    public ValveKeyValues(string key, string? value, IReadOnlyList<ValveKeyValues> children)
    {
        Key = key ?? string.Empty;
        Value = value;
        Children = children ?? [];
    }

    public string Key { get; }

    public string? Value { get; }

    public IReadOnlyList<ValveKeyValues> Children { get; }

    public bool HasChildren => Children.Count > 0;

    /// <summary>Returns the first child section with the given key (case-insensitive).</summary>
    public ValveKeyValues? GetSection(string key)
        => Children.FirstOrDefault(child => child.HasChildren && string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the value of the first child with the given key (case-insensitive).</summary>
    public string? GetValue(string key)
        => Children.FirstOrDefault(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    public static ValveKeyValues Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(text).ParseDocument();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;

        public Parser(string text) => _text = text;

        private bool AtEnd => _index >= _text.Length;

        private char Current => _text[_index];

        public ValveKeyValues ParseDocument() => new(string.Empty, null, ParseEntries(topLevel: true));

        private List<ValveKeyValues> ParseEntries(bool topLevel)
        {
            var entries = new List<ValveKeyValues>();

            while (true)
            {
                SkipTrivia();

                if (AtEnd)
                {
                    if (!topLevel)
                    {
                        throw Error("Unexpected end of document: a section is not closed.");
                    }

                    return entries;
                }

                if (Current == '}')
                {
                    if (topLevel)
                    {
                        throw Error("Unexpected '}'.");
                    }

                    _index++;
                    return entries;
                }

                var key = ReadToken();
                SkipTrivia();

                if (AtEnd)
                {
                    throw Error($"Missing value for key '{key}'.");
                }

                if (Current == '{')
                {
                    _index++;
                    var children = ParseEntries(topLevel: false);
                    entries.Add(new ValveKeyValues(key, null, children));
                }
                else
                {
                    var value = ReadToken();
                    entries.Add(new ValveKeyValues(key, value, []));
                }
            }
        }

        private void SkipTrivia()
        {
            while (!AtEnd)
            {
                var current = Current;

                if (char.IsWhiteSpace(current))
                {
                    _index++;
                    continue;
                }

                if (current == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                {
                    while (!AtEnd && Current != '\n')
                    {
                        _index++;
                    }

                    continue;
                }

                break;
            }
        }

        private string ReadToken()
        {
            if (Current == '"')
            {
                return ReadQuotedToken();
            }

            var start = _index;
            while (!AtEnd)
            {
                var current = Current;
                if (char.IsWhiteSpace(current) || current is '{' or '}' or '"')
                {
                    break;
                }

                _index++;
            }

            if (_index == start)
            {
                throw Error("Expected a key or a value.");
            }

            return _text[start.._index];
        }

        private string ReadQuotedToken()
        {
            _index++; // opening quote
            var builder = new StringBuilder();

            while (!AtEnd)
            {
                var current = Current;

                if (current == '\\' && _index + 1 < _text.Length)
                {
                    var escaped = _text[_index + 1];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => escaped,
                    });
                    _index += 2;
                    continue;
                }

                if (current == '"')
                {
                    _index++;
                    return builder.ToString();
                }

                builder.Append(current);
                _index++;
            }

            throw Error("Unterminated quoted string.");
        }

        private ValveKeyValuesFormatException Error(string message)
        {
            var line = 1;
            for (var i = 0; i < _index && i < _text.Length; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                }
            }

            return new ValveKeyValuesFormatException($"{message} (line {line})");
        }
    }
}
