using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Domain.Configuration;

namespace DarkCipher.AgentControl.Application.Configuration;

/// <summary>Parsed configuration file: flat "section.key" → value view plus the ability to write values back while keeping
/// everything else (comments, order, unrelated keys) exactly as it was.</summary>
public interface IConfigDocument
{
    IReadOnlyDictionary<string, string> Values { get; }
    void Set(string key, string value);
    string Render();
}

public sealed class ConfigFormatException(string message) : Exception(message);

public static class ConfigDocuments
{
    public static IConfigDocument Parse(ConfigFormat format, string? text) => format switch
    {
        ConfigFormat.TomlSubset => TomlSubsetDocument.Parse(text ?? ""),
        _ => JsonConfigDocument.Parse(text),
    };
}

/// <summary>
/// A deliberately small TOML subset: comments, blank lines, [table] headers, and key = value with basic or literal strings,
/// integers, floats and booleans. Anything else (arrays, inline tables, multi-line strings, [[array tables]], dotted keys)
/// makes the file "unsupported": the app then refuses to merge rather than risk damaging it.
/// </summary>
public sealed class TomlSubsetDocument : IConfigDocument
{
    private readonly List<string> _lines;
    private readonly Dictionary<string, (int Line, string Raw)> _index = new(StringComparer.Ordinal);

    private TomlSubsetDocument(List<string> lines) => _lines = lines;

    public IReadOnlyDictionary<string, string> Values => _index.ToDictionary(k => k.Key, k => Decode(k.Value.Raw), StringComparer.Ordinal);

    public static TomlSubsetDocument Parse(string text)
    {
        var doc = new TomlSubsetDocument(text.Replace("\r\n", "\n").Split('\n').ToList());
        if (doc._lines.Count > 0 && doc._lines[^1].Length == 0) doc._lines.RemoveAt(doc._lines.Count - 1);
        var section = "";
        for (var i = 0; i < doc._lines.Count; i++)
        {
            var line = doc._lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("[[", StringComparison.Ordinal)) throw new ConfigFormatException($"Line {i + 1}: array tables are not supported.");
            if (line.StartsWith('['))
            {
                var close = line.IndexOf(']');
                var name = close > 1 ? line[1..close].Trim() : "";
                if (!IsBareKey(name) || (line[(close + 1)..].Trim() is { Length: > 0 } rest && !rest.StartsWith('#')))
                    throw new ConfigFormatException($"Line {i + 1}: unsupported table header.");
                section = name;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new ConfigFormatException($"Line {i + 1}: expected key = value.");
            var key = line[..eq].Trim();
            if (!IsBareKey(key)) throw new ConfigFormatException($"Line {i + 1}: unsupported key '{key}'.");
            var raw = StripComment(line[(eq + 1)..].Trim());
            Decode(raw); // validates
            var full = section.Length == 0 ? key : section + "." + key;
            if (!doc._index.TryAdd(full, (i, raw))) throw new ConfigFormatException($"Line {i + 1}: duplicate key '{full}'.");
        }
        return doc;
    }

    public void Set(string key, string value)
    {
        var encoded = Encode(value);
        if (_index.TryGetValue(key, out var at))
        {
            var original = _lines[at.Line];
            var eq = original.IndexOf('=');
            _lines[at.Line] = original[..(eq + 1)] + " " + encoded;
            _index[key] = (at.Line, encoded);
            return;
        }
        var dot = key.LastIndexOf('.');
        var section = dot < 0 ? "" : key[..dot];
        var name = dot < 0 ? key : key[(dot + 1)..];
        if (!IsBareKey(name) || (section.Length > 0 && !IsBareKey(section))) throw new ConfigFormatException($"Unsupported key '{key}'.");
        int insertAt;
        if (section.Length == 0)
        {
            insertAt = _lines.FindIndex(l => l.TrimStart().StartsWith('['));
            if (insertAt < 0) insertAt = _lines.Count;
        }
        else
        {
            var header = _lines.FindIndex(l => l.Trim() == "[" + section + "]");
            if (header < 0)
            {
                _lines.Add("");
                _lines.Add("[" + section + "]");
                header = _lines.Count - 1;
            }
            insertAt = header + 1;
            while (insertAt < _lines.Count && !_lines[insertAt].TrimStart().StartsWith('[')) insertAt++;
            while (insertAt > header + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        }
        _lines.Insert(insertAt, name + " = " + encoded);
        Reindex();
    }

    private void Reindex()
    {
        var reparsed = Parse(Render());
        _index.Clear();
        foreach (var (k, v) in reparsed._index) _index[k] = v;
        _lines.Clear();
        _lines.AddRange(reparsed._lines);
    }

    public string Render() => string.Join("\n", _lines) + "\n";

    private static bool IsBareKey(string k) => k.Length is > 0 and <= 64 && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string StripComment(string raw)
    {
        var inBasic = false; var inLiteral = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\' && inBasic) { i++; continue; }
            if (c == '"' && !inLiteral) inBasic = !inBasic;
            else if (c == '\'' && !inBasic) inLiteral = !inLiteral;
            else if (c == '#' && !inBasic && !inLiteral) return raw[..i].TrimEnd();
        }
        return raw;
    }

    public static string Decode(string raw)
    {
        if (raw.StartsWith("\"\"\"", StringComparison.Ordinal) || raw.StartsWith("'''", StringComparison.Ordinal)) throw new ConfigFormatException("Multi-line strings are not supported.");
        if (raw.StartsWith('"'))
        {
            if (raw.Length < 2 || !raw.EndsWith('"')) throw new ConfigFormatException("Unterminated string.");
            var sb = new StringBuilder();
            for (var i = 1; i < raw.Length - 1; i++)
            {
                var c = raw[i];
                if (c == '"') throw new ConfigFormatException("Unescaped quote in string.");
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= raw.Length - 1) throw new ConfigFormatException("Bad escape.");
                sb.Append(raw[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\\' => '\\', _ => throw new ConfigFormatException("Unsupported escape.") });
            }
            return sb.ToString();
        }
        if (raw.StartsWith('\''))
        {
            if (raw.Length < 2 || !raw.EndsWith('\'') || raw[1..^1].Contains('\'')) throw new ConfigFormatException("Bad literal string.");
            return raw[1..^1];
        }
        if (raw is "true" or "false") return raw;
        if (long.TryParse(raw.Replace("_", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return raw;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return raw;
        throw new ConfigFormatException($"Unsupported value '{(raw.Length > 20 ? raw[..20] + "…" : raw)}' (arrays and inline tables are not edited by this app).");
    }

    /// <summary>Numbers and booleans are written bare; everything else as a basic string.</summary>
    public static string Encode(string value)
    {
        if (value is "true" or "false") return value;
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return value;
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", _ when c < 0x20 => throw new ConfigFormatException("Control characters are not allowed."), _ => c.ToString() });
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>JSON configuration: keys are dotted paths of object properties; values are strings, numbers or booleans.</summary>
public sealed class JsonConfigDocument : IConfigDocument
{
    private readonly JsonObject _root;
    private JsonConfigDocument(JsonObject root) => _root = root;

    public static JsonConfigDocument Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonConfigDocument([]);
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
                ? new JsonConfigDocument(o) : throw new ConfigFormatException("The file is not a JSON object.");
        }
        catch (JsonException ex) { throw new ConfigFormatException("Invalid JSON: " + ex.Message.Split('\n')[0]); }
    }

    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            void Walk(JsonObject o, string prefix)
            {
                foreach (var (k, v) in o)
                {
                    var key = prefix.Length == 0 ? k : prefix + "." + k;
                    if (v is JsonObject child) Walk(child, key);
                    else if (v is JsonValue val) d[key] = val.GetValueKind() == JsonValueKind.String ? val.GetValue<string>() : val.ToJsonString();
                }
            }
            Walk(_root, "");
            return d;
        }
    }

    public void Set(string key, string value)
    {
        var parts = key.Split('.');
        var o = _root;
        foreach (var p in parts[..^1])
        {
            if (o[p] is JsonObject child) o = child;
            else if (o[p] is null) { var n = new JsonObject(); o[p] = n; o = n; }
            else throw new ConfigFormatException($"'{p}' is not an object; '{key}' cannot be set without replacing user data.");
        }
        o[parts[^1]] = value is "true" or "false" ? JsonValue.Create(value == "true")
            : long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l)
            : JsonValue.Create(value);
    }

    public string Render() => _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
