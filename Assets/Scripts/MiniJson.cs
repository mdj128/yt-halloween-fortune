using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Minimal JSON parser/serializer (Unity-compatible).
/// </summary>
public static class MiniJson
{
    public static object Deserialize(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        return Parser.Parse(json);
    }

    private sealed class Parser : IDisposable
    {
        private const string WordBreak = "{}[],:\"";
        private readonly StringReader _reader;

        private Parser(string json)
        {
            _reader = new StringReader(json);
        }

        public static object Parse(string json)
        {
            using var instance = new Parser(json);
            return instance.ParseValue();
        }

        public void Dispose()
        {
            _reader.Dispose();
        }

        private Dictionary<string, object> ParseObject()
        {
            var table = new Dictionary<string, object>();
            ConsumeToken(); // {

            while (true)
            {
                switch (PeekToken())
                {
                    case Token.None:
                        return null;
                    case Token.CurlyClose:
                        ConsumeToken();
                        return table;
                    default:
                        // name
                        string name = ParseString();
                        if (name == null)
                        {
                            return null;
                        }

                        // :
                        if (PeekToken() != Token.Colon)
                        {
                            return null;
                        }
                        ConsumeToken();

                        // value
                        table[name] = ParseValue();
                        break;
                }
            }
        }

        private List<object> ParseArray()
        {
            var array = new List<object>();
            ConsumeToken(); // [

            while (true)
            {
                switch (PeekToken())
                {
                    case Token.None:
                        return null;
                    case Token.SquaredClose:
                        ConsumeToken();
                        return array;
                    default:
                        array.Add(ParseValue());
                        break;
                }
            }
        }

        private object ParseValue()
        {
            switch (PeekToken())
            {
                case Token.String:
                    return ParseString();
                case Token.Number:
                    return ParseNumber();
                case Token.CurlyOpen:
                    return ParseObject();
                case Token.SquaredOpen:
                    return ParseArray();
                case Token.True:
                    ConsumeToken();
                    return true;
                case Token.False:
                    ConsumeToken();
                    return false;
                case Token.Null:
                    ConsumeToken();
                    return null;
                case Token.None:
                    return null;
                default:
                    return null;
            }
        }

        private string ParseString()
        {
            var sb = new StringBuilder();
            ConsumeToken(); // "

            while (true)
            {
                if (_reader.Peek() == -1)
                {
                    return null;
                }

                char c = NextChar;
                if (c == '"')
                {
                    break;
                }

                if (c == '\\')
                {
                    if (_reader.Peek() == -1)
                    {
                        return null;
                    }

                    c = NextChar;
                    switch (c)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            sb.Append(c);
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            var hex = new char[4];
                            for (int i = 0; i < 4; i++)
                            {
                                if (_reader.Peek() == -1)
                                {
                                    return null;
                                }
                                hex[i] = NextChar;
                            }
                            sb.Append((char)Convert.ToInt32(new string(hex), 16));
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private object ParseNumber()
        {
            string number = NextWord;
            if (number.IndexOf('.') == -1)
            {
                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                {
                    return parsedInt;
                }
            }

            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
            {
                return parsedDouble;
            }

            return 0;
        }

        private void EatWhitespace()
        {
            while (char.IsWhiteSpace(PeekChar))
            {
                ConsumeChar();
                if (_reader.Peek() == -1)
                {
                    break;
                }
            }
        }

        private char PeekChar => (char)_reader.Peek();

        private char NextChar => (char)_reader.Read();

        private string NextWord
        {
            get
            {
                var sb = new StringBuilder();
                while (!IsWordBreak(PeekChar))
                {
                    sb.Append(NextChar);
                    if (_reader.Peek() == -1)
                    {
                        break;
                    }
                }
                return sb.ToString();
            }
        }

        private Token PeekToken()
        {
            EatWhitespace();
            if (_reader.Peek() == -1)
            {
                return Token.None;
            }

            char c = PeekChar;
            switch (c)
            {
                case '{': return Token.CurlyOpen;
                case '}': return Token.CurlyClose;
                case '[': return Token.SquaredOpen;
                case ']': return Token.SquaredClose;
                case ',': return Token.Comma;
                case '"': return Token.String;
                case ':': return Token.Colon;
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                case '-':
                    return Token.Number;
            }

            string word = NextWord;
            switch (word)
            {
                case "false": return Token.False;
                case "true": return Token.True;
                case "null": return Token.Null;
            }

            return Token.None;
        }

        private void ConsumeToken()
        {
            PeekToken();
            ConsumeChar();
        }

        private void ConsumeChar()
        {
            _reader.Read();
        }

        private static bool IsWordBreak(char c)
        {
            return char.IsWhiteSpace(c) || WordBreak.IndexOf(c) != -1;
        }

        private enum Token
        {
            None,
            CurlyOpen,
            CurlyClose,
            SquaredOpen,
            SquaredClose,
            Colon,
            Comma,
            String,
            Number,
            True,
            False,
            Null
        }
    }

    private sealed class StringReader : IDisposable
    {
        private readonly string _s;
        private int _position;

        public StringReader(string s)
        {
            _s = s;
            _position = 0;
        }

        public int Peek()
        {
            return _position < _s.Length ? _s[_position] : -1;
        }

        public int Read()
        {
            return _position < _s.Length ? _s[_position++] : -1;
        }

        public void Dispose()
        {
        }
    }
}
