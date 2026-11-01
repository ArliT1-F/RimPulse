using System.Globalization;
using System.Text;

namespace RimPulse.Core.Build;

/// <summary>
/// Evaluates the subset of MSBuild <c>Condition</c> syntax a mod project uses in practice, so a property
/// group such as <c>Condition="'$(OS)' == 'Windows_NT'"</c> or an import guarded by
/// <c>Condition="Exists('$(MSBuildThisFileDirectory)local.props')"</c> applies when it should.
/// </summary>
/// <remarks>
/// <para>Supported: <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>and</c>,
/// <c>or</c>, <c>!</c>, parentheses, quoted and bare values, property references, and the functions
/// <c>Exists()</c> and <c>HasTrailingSlash()</c>.</para>
/// <para>
/// Anything else — item references, string functions, properties that only MSBuild's own targets define — is
/// reported as unsupported rather than guessed at, and the element it guards is ignored.
/// </para>
/// <para>
/// String comparisons are case-insensitive and an undefined property is the empty string, both as in MSBuild.
/// </para>
/// </remarks>
internal static class MsBuildCondition
{
    /// <summary>
    /// Evaluates <paramref name="condition"/>. Returns false (with a <paramref name="reason"/>) when the
    /// condition uses syntax RimPulse does not evaluate; <paramref name="result"/> is then meaningless.
    /// </summary>
    public static bool TryEvaluate(
        string condition,
        MsBuildPropertySet properties,
        string directory,
        out bool result,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var parser = new ConditionParser(condition ?? string.Empty, properties, directory);

        try
        {
            result = parser.Parse();
            reason = null;
            return true;
        }
        catch (ConditionFormatException ex)
        {
            result = false;
            reason = ex.Message;
            return false;
        }
    }

    private sealed class ConditionFormatException(string message) : Exception(message)
    {
    }

    /// <summary>A small recursive-descent parser: <c>or</c> → <c>and</c> → <c>!</c> → comparison or value.</summary>
    private sealed class ConditionParser(string text, MsBuildPropertySet properties, string directory)
    {
        private int _index;

        public bool Parse()
        {
            var value = ParseOr();
            SkipWhitespace();

            if (_index < text.Length)
            {
                throw new ConditionFormatException($"'{text[_index..].Trim()}' could not be read");
            }

            return value;
        }

        private bool ParseOr()
        {
            var value = ParseAnd();

            while (TryConsume("or"))
            {
                // Parsed before combining: a short-circuit would skip the syntax check on the right side.
                var right = ParseAnd();
                value = value || right;
            }

            return value;
        }

        private bool ParseAnd()
        {
            var value = ParseNot();

            while (TryConsume("and"))
            {
                var right = ParseNot();
                value = value && right;
            }

            return value;
        }

        private bool ParseNot()
        {
            SkipWhitespace();

            if (_index < text.Length && text[_index] == '!')
            {
                _index++;
                return !ParseNot();
            }

            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            SkipWhitespace();

            if (TryConsume("("))
            {
                var value = ParseOr();

                if (!TryConsume(")"))
                {
                    throw new ConditionFormatException("a '(' was not closed");
                }

                return value;
            }

            if (IsFunctionCall())
            {
                return ParseFunction();
            }

            var left = ParseOperand();

            if (TryReadComparison() is { } comparison)
            {
                return Compare(left, comparison, ParseOperand());
            }

            if (bool.TryParse(left, out var literal))
            {
                return literal;
            }

            throw new ConditionFormatException($"'{left}' is not a complete condition");
        }

        private string ParseOperand()
        {
            SkipWhitespace();

            if (_index >= text.Length)
            {
                throw new ConditionFormatException("the condition ends where a value was expected");
            }

            var current = text[_index];

            if (current is '\'' or '"')
            {
                return ReadQuoted(current);
            }

            if (IsPropertyReferenceAt(_index))
            {
                var end = text.IndexOf(')', _index + 2);

                if (end < 0)
                {
                    throw new ConditionFormatException("a property reference was not closed");
                }

                var name = text[(_index + 2)..end].Trim();
                _index = end + 1;

                // Inside a condition an undefined property is the empty string, exactly as in MSBuild.
                return properties.Get(name) ?? string.Empty;
            }

            var start = _index;

            // A bare value ends at whitespace, a bracket, or an operator.
            while (_index < text.Length && !char.IsWhiteSpace(text[_index])
                   && text[_index] is not ('(' or ')' or '=' or '!' or '<' or '>'))
            {
                _index++;
            }

            if (_index == start)
            {
                throw new ConditionFormatException($"'{text[start..].Trim()}' could not be read");
            }

            return text[start.._index];
        }

        private string ReadQuoted(char quote)
        {
            _index++;
            var builder = new StringBuilder();

            while (_index < text.Length)
            {
                var current = text[_index];

                if (current == quote)
                {
                    _index++;
                    return builder.ToString();
                }

                if (IsPropertyReferenceAt(_index))
                {
                    var end = text.IndexOf(')', _index + 2);

                    if (end < 0)
                    {
                        throw new ConditionFormatException("a property reference was not closed");
                    }

                    var name = text[(_index + 2)..end].Trim();
                    builder.Append(properties.Get(name) ?? string.Empty);
                    _index = end + 1;
                    continue;
                }

                if ((current is '@' or '%') && _index + 1 < text.Length && text[_index + 1] == '(')
                {
                    throw new ConditionFormatException(
                        "an item or metadata reference such as '@(Compile)' is not evaluated");
                }

                builder.Append(current);
                _index++;
            }

            throw new ConditionFormatException("a quoted value was not closed");
        }

        private bool IsPropertyReferenceAt(int index)
            => index + 1 < text.Length && text[index] == '$' && text[index + 1] == '(';

        private bool IsFunctionCall()
        {
            var probe = _index;

            while (probe < text.Length && (char.IsLetterOrDigit(text[probe]) || text[probe] is '_' or '.'))
            {
                probe++;
            }

            if (probe == _index)
            {
                return false;
            }

            while (probe < text.Length && char.IsWhiteSpace(text[probe]))
            {
                probe++;
            }

            return probe < text.Length && text[probe] == '(';
        }

        private bool ParseFunction()
        {
            var start = _index;

            while (_index < text.Length && text[_index] != '(')
            {
                _index++;
            }

            var name = text[start.._index].Trim();
            _index++; // the '('

            var argument = ParseOperand();

            if (!TryConsume(")"))
            {
                throw new ConditionFormatException($"the call to {name}() was not closed");
            }

            return name.ToUpperInvariant() switch
            {
                "EXISTS" => Exists(argument),
                "HASTRAILINGSLASH" => argument.EndsWith('/') || argument.EndsWith('\\'),
                _ => throw new ConditionFormatException($"the function {name}() is not supported"),
            };
        }

        private bool Exists(string path)
        {
            if (path.Length == 0)
            {
                return false;
            }

            var resolved = MsBuildPropertySet.ResolvePath(directory, path);
            return resolved is not null && (File.Exists(resolved) || Directory.Exists(resolved));
        }

        /// <summary>Reads and consumes a comparison operator, or returns null when there is none.</summary>
        private string? TryReadComparison()
        {
            SkipWhitespace();

            string? found = null;

            if (_index + 1 < text.Length)
            {
                var pair = text.Substring(_index, 2);

                if (pair is "==" or "!=" or "<=" or ">=")
                {
                    found = pair;
                }
            }

            if (found is null && _index < text.Length && text[_index] is '<' or '>')
            {
                found = text[_index].ToString();
            }

            if (found is null)
            {
                return null;
            }

            _index += found.Length;
            return found;
        }

        private static bool Compare(string left, string comparison, string right) => comparison switch
        {
            "==" => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "<" => Number(left) < Number(right),
            ">" => Number(left) > Number(right),
            "<=" => Number(left) <= Number(right),
            ">=" => Number(left) >= Number(right),
            _ => throw new ConditionFormatException($"'{comparison}' is not a comparison RimPulse evaluates"),
        };

        private static double Number(string value)
            => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new ConditionFormatException(
                    $"'{value}' is not a number, so it cannot be compared with '<' or '>'");

        private bool TryConsume(string token)
        {
            SkipWhitespace();

            if (_index + token.Length > text.Length
                || string.Compare(text, _index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            // 'or' and 'and' must be whole words, so that a value such as 'orange' is not read as an operator.
            if (char.IsLetter(token[0]))
            {
                var after = _index + token.Length;

                if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] is '_' or '.'))
                {
                    return false;
                }
            }

            _index += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < text.Length && char.IsWhiteSpace(text[_index]))
            {
                _index++;
            }
        }
    }
}
