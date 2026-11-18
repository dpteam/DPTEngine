//
// DPT.Engine - JSON reader and writer.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DPT.Engine.Shared
{
    /// <summary>
    /// A small JSON DOM.
    ///
    /// Hand-written rather than a package, for the same reason as everything else
    /// here: the runtime has to be a single file that runs on .NET Framework 4.0,
    /// and the editor API, the project files and the master's directory all need
    /// JSON. This is the smallest thing that covers those three uses.
    ///
    /// Two deliberate choices:
    ///
    /// * <b>Strict parsing.</b> No comments, no trailing commas, no unquoted keys.
    ///   Project files are written by the editor and read by the runtime; a
    ///   lenient parser would let the editor emit something no other tool accepts.
    ///
    /// * <b>Insertion order is preserved.</b> A project file is diffed in git, and
    ///   a dictionary that reorders members on save makes every commit unreadable.
    ///
    /// Numbers are kept as double, which is exact for every integer up to 2^53 -
    /// far more than a frame number or a vertex index needs.
    /// </summary>
    public sealed class JsonValue
    {
        public enum Kind
        {
            Null,
            Bool,
            Number,
            String,
            Array,
            Object
        }

        private readonly Kind kind;
        private bool boolValue;
        private double numberValue;
        private string stringValue;
        private List<JsonValue> array;
        private List<KeyValuePair<string, JsonValue>> members;

        private JsonValue(Kind kind)
        {
            this.kind = kind;
        }

        public Kind Type
        {
            get { return kind; }
        }

        public bool IsNull
        {
            get { return kind == Kind.Null; }
        }

        // ---------------------------------------------------------------- factories

        public static JsonValue Null()
        {
            return new JsonValue(Kind.Null);
        }

        public static JsonValue From(bool value)
        {
            var result = new JsonValue(Kind.Bool);
            result.boolValue = value;
            return result;
        }

        public static JsonValue From(double value)
        {
            var result = new JsonValue(Kind.Number);
            result.numberValue = value;
            return result;
        }

        public static JsonValue From(int value)
        {
            return From((double)value);
        }

        public static JsonValue From(string value)
        {
            if (value == null) return Null();

            var result = new JsonValue(Kind.String);
            result.stringValue = value;
            return result;
        }

        public static JsonValue NewArray()
        {
            var result = new JsonValue(Kind.Array);
            result.array = new List<JsonValue>();
            return result;
        }

        public static JsonValue NewObject()
        {
            var result = new JsonValue(Kind.Object);
            result.members = new List<KeyValuePair<string, JsonValue>>();
            return result;
        }

        // ---------------------------------------------------------------- accessors

        public int Count
        {
            get
            {
                if (kind == Kind.Array) return array.Count;
                if (kind == Kind.Object) return members.Count;
                return 0;
            }
        }

        /// <summary>Array element. Returns a null value when out of range, never throws.</summary>
        public JsonValue this[int index]
        {
            get
            {
                if (kind != Kind.Array || index < 0 || index >= array.Count) return Null();
                return array[index];
            }
        }

        /// <summary>Object member. Returns a null value when absent, never throws.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (kind != Kind.Object) return Null();

                for (var i = 0; i < members.Count; i++)
                {
                    if (string.Equals(members[i].Key, name, StringComparison.Ordinal))
                    {
                        return members[i].Value;
                    }
                }

                return Null();
            }
        }

        public bool Has(string name)
        {
            if (kind != Kind.Object) return false;

            for (var i = 0; i < members.Count; i++)
            {
                if (string.Equals(members[i].Key, name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        public IEnumerable<JsonValue> Items
        {
            get
            {
                if (kind != Kind.Array) return new JsonValue[0];
                return array;
            }
        }

        public IEnumerable<KeyValuePair<string, JsonValue>> Members
        {
            get
            {
                if (kind != Kind.Object) return new KeyValuePair<string, JsonValue>[0];
                return members;
            }
        }

        public JsonValue Add(JsonValue value)
        {
            if (kind != Kind.Array) throw new InvalidOperationException("This JSON value is not an array.");
            array.Add(value ?? Null());
            return this;
        }

        public JsonValue Add(string value)
        {
            return Add(From(value));
        }

        public JsonValue Add(double value)
        {
            return Add(From(value));
        }

        public JsonValue Set(string name, JsonValue value)
        {
            if (kind != Kind.Object) throw new InvalidOperationException("This JSON value is not an object.");

            var stored = value ?? Null();

            for (var i = 0; i < members.Count; i++)
            {
                if (string.Equals(members[i].Key, name, StringComparison.Ordinal))
                {
                    members[i] = new KeyValuePair<string, JsonValue>(name, stored);
                    return this;
                }
            }

            members.Add(new KeyValuePair<string, JsonValue>(name, stored));
            return this;
        }

        public JsonValue Set(string name, string value)
        {
            return Set(name, From(value));
        }

        public JsonValue Set(string name, double value)
        {
            return Set(name, From(value));
        }

        public JsonValue Set(string name, bool value)
        {
            return Set(name, From(value));
        }

        public string AsString(string fallback)
        {
            if (kind == Kind.String) return stringValue;
            if (kind == Kind.Number) return numberValue.ToString(CultureInfo.InvariantCulture);
            if (kind == Kind.Bool) return boolValue ? "true" : "false";
            return fallback;
        }

        public string AsString()
        {
            return AsString("");
        }

        public double AsDouble(double fallback)
        {
            if (kind == Kind.Number) return numberValue;
            if (kind == Kind.Bool) return boolValue ? 1 : 0;

            if (kind == Kind.String)
            {
                double parsed;
                if (double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        public int AsInt(int fallback)
        {
            var value = AsDouble(fallback);

            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;

            return (int)Math.Round(value);
        }

        public float AsFloat(float fallback)
        {
            return (float)AsDouble(fallback);
        }

        public bool AsBool(bool fallback)
        {
            if (kind == Kind.Bool) return boolValue;
            if (kind == Kind.Number) return numberValue != 0;

            if (kind == Kind.String)
            {
                return string.Equals(stringValue, "true", StringComparison.OrdinalIgnoreCase);
            }

            return fallback;
        }

        // ---------------------------------------------------------------- writing

        public override string ToString()
        {
            return ToJson(false);
        }

        public string ToJson(bool pretty)
        {
            var builder = new StringBuilder(512);
            Write(builder, pretty, 0);
            return builder.ToString();
        }

        private void Write(StringBuilder builder, bool pretty, int depth)
        {
            switch (kind)
            {
                case Kind.Null:
                    builder.Append("null");
                    break;

                case Kind.Bool:
                    builder.Append(boolValue ? "true" : "false");
                    break;

                case Kind.Number:
                    WriteNumber(builder, numberValue);
                    break;

                case Kind.String:
                    WriteString(builder, stringValue);
                    break;

                case Kind.Array:
                    WriteArray(builder, pretty, depth);
                    break;

                case Kind.Object:
                    WriteObject(builder, pretty, depth);
                    break;
            }
        }

        private void WriteArray(StringBuilder builder, bool pretty, int depth)
        {
            if (array.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            builder.Append('[');

            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0) builder.Append(',');
                NewLine(builder, pretty, depth + 1);
                array[i].Write(builder, pretty, depth + 1);
            }

            NewLine(builder, pretty, depth);
            builder.Append(']');
        }

        private void WriteObject(StringBuilder builder, bool pretty, int depth)
        {
            if (members.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            builder.Append('{');

            for (var i = 0; i < members.Count; i++)
            {
                if (i > 0) builder.Append(',');
                NewLine(builder, pretty, depth + 1);
                WriteString(builder, members[i].Key);
                builder.Append(':');
                if (pretty) builder.Append(' ');
                members[i].Value.Write(builder, pretty, depth + 1);
            }

            NewLine(builder, pretty, depth);
            builder.Append('}');
        }

        private static void NewLine(StringBuilder builder, bool pretty, int depth)
        {
            if (!pretty) return;

            builder.Append('\n');
            builder.Append(' ', depth * 2);
        }

        private static void WriteNumber(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                // JSON has no representation for these. Writing 0 keeps the file
                // parseable; the alternative is a project file no tool can read.
                builder.Append('0');
                return;
            }

            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            {
                builder.Append(((long)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');

            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];

                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        // ---------------------------------------------------------------- parsing

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            var parser = new Parser(text);
            var value = parser.ReadValue(0);
            parser.SkipWhitespace();

            if (!parser.AtEnd)
            {
                throw parser.Error("trailing characters after the root value");
            }

            return value;
        }

        /// <summary>Parses, returning <paramref name="fallback"/> instead of throwing.</summary>
        public static JsonValue TryParse(string text, JsonValue fallback)
        {
            try
            {
                return Parse(text);
            }
            catch (FormatException)
            {
                return fallback;
            }
        }

        private sealed class Parser
        {
            private const int MaxDepth = 64;

            private readonly string text;
            private int position;

            internal Parser(string text)
            {
                this.text = text;
            }

            internal bool AtEnd
            {
                get { return position >= text.Length; }
            }

            internal FormatException Error(string message)
            {
                var line = 1;
                var column = 1;

                for (var i = 0; i < position && i < text.Length; i++)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }

                return new FormatException("JSON: " + message + " at line " + line + ", column " + column + ".");
            }

            internal void SkipWhitespace()
            {
                while (position < text.Length)
                {
                    var ch = text[position];

                    if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                    {
                        position++;
                        continue;
                    }

                    break;
                }
            }

            internal JsonValue ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("nesting is too deep");

                SkipWhitespace();
                if (AtEnd) throw Error("unexpected end of input");

                var ch = text[position];

                switch (ch)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return From(ReadString());
                    case 't': Expect("true"); return From(true);
                    case 'f': Expect("false"); return From(false);
                    case 'n': Expect("null"); return Null();
                }

                if (ch == '-' || (ch >= '0' && ch <= '9')) return ReadNumber();

                throw Error("unexpected character '" + ch + "'");
            }

            private void Expect(string literal)
            {
                if (position + literal.Length > text.Length ||
                    string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
                {
                    throw Error("expected '" + literal + "'");
                }

                position += literal.Length;
            }

            private JsonValue ReadObject(int depth)
            {
                var result = NewObject();
                position++; // '{'
                SkipWhitespace();

                if (!AtEnd && text[position] == '}')
                {
                    position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();

                    if (AtEnd || text[position] != '"') throw Error("expected a member name");

                    var name = ReadString();
                    SkipWhitespace();

                    if (AtEnd || text[position] != ':') throw Error("expected ':' after a member name");
                    position++;

                    result.Set(name, ReadValue(depth + 1));

                    SkipWhitespace();
                    if (AtEnd) throw Error("unexpected end of object");

                    if (text[position] == ',')
                    {
                        position++;
                        continue;
                    }

                    if (text[position] == '}')
                    {
                        position++;
                        return result;
                    }

                    throw Error("expected ',' or '}'");
                }
            }

            private JsonValue ReadArray(int depth)
            {
                var result = NewArray();
                position++; // '['
                SkipWhitespace();

                if (!AtEnd && text[position] == ']')
                {
                    position++;
                    return result;
                }

                while (true)
                {
                    result.Add(ReadValue(depth + 1));

                    SkipWhitespace();
                    if (AtEnd) throw Error("unexpected end of array");

                    if (text[position] == ',')
                    {
                        position++;
                        continue;
                    }

                    if (text[position] == ']')
                    {
                        position++;
                        return result;
                    }

                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                position++; // opening quote
                var builder = new StringBuilder();

                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");

                    var ch = text[position++];

                    if (ch == '"') return builder.ToString();

                    if (ch != '\\')
                    {
                        builder.Append(ch);
                        continue;
                    }

                    if (AtEnd) throw Error("unterminated escape");

                    var escape = text[position++];

                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (position + 4 > text.Length) throw Error("short \\u escape");

                            int code;
                            if (!int.TryParse(text.Substring(position, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out code))
                            {
                                throw Error("bad \\u escape");
                            }

                            builder.Append((char)code);
                            position += 4;
                            break;

                        default:
                            throw Error("unknown escape '\\" + escape + "'");
                    }
                }
            }

            private JsonValue ReadNumber()
            {
                var start = position;

                if (position < text.Length && text[position] == '-') position++;

                while (position < text.Length)
                {
                    var ch = text[position];

                    if ((ch >= '0' && ch <= '9') || ch == '.' || ch == 'e' || ch == 'E' ||
                        ch == '+' || ch == '-')
                    {
                        position++;
                        continue;
                    }

                    break;
                }

                var slice = text.Substring(start, position - start);
                double value;

                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    position = start;
                    throw Error("bad number '" + slice + "'");
                }

                return From(value);
            }
        }
    }
}
