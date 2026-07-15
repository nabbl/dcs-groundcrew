using System.Globalization;
using System.Text;

namespace DcsDashboard.Api.Services;

internal sealed class LuaDataValue
{
    public object? Scalar { get; }
    public Dictionary<string, LuaDataValue>? Fields { get; }
    public bool IsTable => Fields is not null;

    public LuaDataValue(object? scalar) => Scalar = scalar;
    public LuaDataValue(Dictionary<string, LuaDataValue> fields) => Fields = fields;
    public LuaDataValue? Get(string key) => Fields?.GetValueOrDefault(key);
    public IEnumerable<LuaDataValue> Values => Fields?.Values ?? Enumerable.Empty<LuaDataValue>();
    public string? AsString() => Scalar as string;
    public int? AsInteger() => Scalar switch
    {
        double value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        string value when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}

internal static class LuaDataParser
{
    public static LuaDataParseResult ParseAssignmentBestEffort(string input)
    {
        var parser = new Parser(input);
        if (parser.Peek().Kind == TokenKind.Identifier)
        {
            parser.Take();
            parser.Expect(TokenKind.Equals);
        }
        var result = parser.ParseValue();
        return new LuaDataParseResult(result, parser.SkippedFields);
    }

    private sealed class Parser
    {
        private const int MaximumDepth = 128;
        private const int MaximumValues = 1_000_000;
        private readonly Lexer _lexer;
        private readonly Queue<Token> _tokens = new();
        private int _valueCount;
        public int SkippedFields { get; private set; }

        public Parser(string input) => _lexer = new Lexer(input);

        public Token Peek(int offset = 0)
        {
            while (_tokens.Count <= offset) _tokens.Enqueue(_lexer.Next());
            return _tokens.ElementAt(offset);
        }

        public Token Take()
        {
            var token = Peek();
            _tokens.Dequeue();
            return token;
        }

        public void Expect(TokenKind kind)
        {
            var token = Take();
            if (token.Kind != kind) throw new FormatException($"Expected {kind} but found {token.Kind} at character {token.Position}.");
        }

        public LuaDataValue ParseValue(int depth = 0)
        {
            if (depth > MaximumDepth) throw new InvalidDataException($"Lua data nesting exceeds the supported depth of {MaximumDepth}.");
            if (++_valueCount > MaximumValues) throw new InvalidDataException($"Lua data contains more than {MaximumValues:N0} values.");
            var token = Take();
            return token.Kind switch
            {
                TokenKind.LeftBrace => ParseTable(depth),
                TokenKind.String => new LuaDataValue(token.Text),
                TokenKind.Number => new LuaDataValue(double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture)),
                TokenKind.True => new LuaDataValue(true),
                TokenKind.False => new LuaDataValue(false),
                TokenKind.Nil => new LuaDataValue((object?)null),
                _ => throw new FormatException($"Expected a Lua data value but found {token.Kind} at character {token.Position}."),
            };
        }

        private LuaDataValue ParseTable(int depth)
        {
            var fields = new Dictionary<string, LuaDataValue>(StringComparer.Ordinal);
            var positionalIndex = 1;
            while (Peek().Kind is not TokenKind.RightBrace and not TokenKind.End)
            {
                try
                {
                    string key;
                    LuaDataValue value;
                    if (Peek().Kind == TokenKind.LeftBracket)
                    {
                        Take();
                        var keyValue = ParseValue(depth + 1);
                        Expect(TokenKind.RightBracket);
                        Expect(TokenKind.Equals);
                        key = KeyText(keyValue, positionalIndex++);
                        value = ParseValue(depth + 1);
                    }
                    else if (Peek().Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Equals)
                    {
                        key = Take().Text;
                        Take();
                        value = ParseValue(depth + 1);
                    }
                    else
                    {
                        key = (positionalIndex++).ToString(CultureInfo.InvariantCulture);
                        value = ParseValue(depth + 1);
                    }
                    fields[key] = value;
                    if (Peek().Kind is TokenKind.Comma or TokenKind.Semicolon) Take();
                }
                catch (Exception exception) when (exception is FormatException or OverflowException)
                {
                    SkippedFields++;
                    RecoverTableField();
                }
            }
            if (Peek().Kind == TokenKind.RightBrace) Take();
            else SkippedFields++;
            return new LuaDataValue(fields);
        }

        private void RecoverTableField()
        {
            var braceDepth = 0;
            var bracketDepth = 0;
            var parenthesisDepth = 0;
            while (true)
            {
                var kind = Peek().Kind;
                if (kind == TokenKind.End) return;
                if (kind == TokenKind.RightBrace && braceDepth == 0 && bracketDepth == 0 && parenthesisDepth == 0) return;
                kind = Take().Kind;
                switch (kind)
                {
                    case TokenKind.LeftBrace: braceDepth++; break;
                    case TokenKind.RightBrace when braceDepth > 0: braceDepth--; break;
                    case TokenKind.LeftBracket: bracketDepth++; break;
                    case TokenKind.RightBracket when bracketDepth > 0: bracketDepth--; break;
                    case TokenKind.LeftParenthesis: parenthesisDepth++; break;
                    case TokenKind.RightParenthesis when parenthesisDepth > 0: parenthesisDepth--; break;
                    case TokenKind.Comma or TokenKind.Semicolon when braceDepth == 0 && bracketDepth == 0 && parenthesisDepth == 0: return;
                }
            }
        }

        private static string KeyText(LuaDataValue value, int fallback) => value.Scalar switch
        {
            string text => text,
            double number => number.ToString("0.################", CultureInfo.InvariantCulture),
            _ => fallback.ToString(CultureInfo.InvariantCulture),
        };
    }

    private sealed class Lexer
    {
        private readonly string _input;
        private int _position;

        public Lexer(string input) => _input = input;

        public Token Next()
        {
            SkipTrivia();
            if (_position >= _input.Length) return new Token(TokenKind.End, "", _position);
            var start = _position;
            var current = _input[_position++];
            switch (current)
            {
                case '{': return new Token(TokenKind.LeftBrace, "{", start);
                case '}': return new Token(TokenKind.RightBrace, "}", start);
                case ']': return new Token(TokenKind.RightBracket, "]", start);
                case '=': return new Token(TokenKind.Equals, "=", start);
                case ',': return new Token(TokenKind.Comma, ",", start);
                case ';': return new Token(TokenKind.Semicolon, ";", start);
                case '(': return new Token(TokenKind.LeftParenthesis, "(", start);
                case ')': return new Token(TokenKind.RightParenthesis, ")", start);
                case '[':
                    if (TryReadLongBracket(start, out var longText)) return new Token(TokenKind.String, longText, start);
                    return new Token(TokenKind.LeftBracket, "[", start);
                case '"':
                case '\'':
                    return new Token(TokenKind.String, ReadQuoted(current, start), start);
            }

            if (char.IsDigit(current) || current == '-' && _position < _input.Length && (char.IsDigit(_input[_position]) || _input[_position] == '.'))
            {
                while (_position < _input.Length && "0123456789.eE+-".Contains(_input[_position], StringComparison.Ordinal)) _position++;
                return new Token(TokenKind.Number, _input[start.._position], start);
            }
            if (char.IsLetter(current) || current == '_')
            {
                while (_position < _input.Length && (char.IsLetterOrDigit(_input[_position]) || _input[_position] == '_')) _position++;
                var text = _input[start.._position];
                return new Token(text switch { "true" => TokenKind.True, "false" => TokenKind.False, "nil" => TokenKind.Nil, _ => TokenKind.Identifier }, text, start);
            }
            return new Token(TokenKind.Unknown, current.ToString(), start);
        }

        private void SkipTrivia()
        {
            while (_position < _input.Length)
            {
                if (char.IsWhiteSpace(_input[_position])) { _position++; continue; }
                if (_position + 1 >= _input.Length || _input[_position] != '-' || _input[_position + 1] != '-') break;
                _position += 2;
                if (_position < _input.Length && _input[_position] == '[' && TryReadLongBracket(_position, out _)) continue;
                while (_position < _input.Length && _input[_position] is not '\r' and not '\n') _position++;
            }
        }

        private bool TryReadLongBracket(int openingPosition, out string value)
        {
            value = "";
            var cursor = openingPosition + 1;
            while (cursor < _input.Length && _input[cursor] == '=') cursor++;
            if (cursor >= _input.Length || _input[cursor] != '[') return false;
            var equalsCount = cursor - openingPosition - 1;
            var contentStart = cursor + 1;
            var terminator = $"]{new string('=', equalsCount)}]";
            var end = _input.IndexOf(terminator, contentStart, StringComparison.Ordinal);
            if (end < 0) throw new FormatException($"Unterminated Lua long string at character {openingPosition}.");
            value = _input[contentStart..end];
            _position = end + terminator.Length;
            return true;
        }

        private string ReadQuoted(char quote, int start)
        {
            var builder = new StringBuilder();
            while (_position < _input.Length)
            {
                var current = _input[_position++];
                if (current == quote) return builder.ToString();
                if (current != '\\' || _position >= _input.Length) { builder.Append(current); continue; }
                current = _input[_position++];
                builder.Append(current switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"', '\'' => '\'', _ => current });
            }
            throw new FormatException($"Unterminated Lua string at character {start}.");
        }
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);
    private enum TokenKind { End, Unknown, Identifier, String, Number, True, False, Nil, LeftBrace, RightBrace, LeftBracket, RightBracket, LeftParenthesis, RightParenthesis, Equals, Comma, Semicolon }
}

internal sealed record LuaDataParseResult(LuaDataValue Value, int SkippedFields);
