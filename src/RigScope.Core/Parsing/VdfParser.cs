using System.Text;

namespace RigScope.Core.Parsing;

/// <summary>
/// A node in Valve's KeyValues text format (Steam .vdf/.acf, CS2 .vcfg and cs2_video.txt).
/// Keys are case-insensitive. When a key repeats, the last value wins, which matches how Steam reads these files.
/// </summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public VdfNode? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;

    /// <summary>Follows a path of child keys; the last segment may be a value or a child.</summary>
    public VdfNode? ChildPath(params string[] keys)
    {
        VdfNode? node = this;
        foreach (var k in keys)
        {
            node = node?.Child(k);
            if (node is null) return null;
        }
        return node;
    }
}

public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;
        int i = 0;

        while (true)
        {
            var token = NextToken(text, ref i, out var kind);
            if (kind == TokenKind.End) break;

            switch (kind)
            {
                case TokenKind.Open:
                    var child = new VdfNode();
                    stack.Peek().Children[pendingKey ?? ""] = child;
                    stack.Push(child);
                    pendingKey = null;
                    break;
                case TokenKind.Close:
                    if (stack.Count > 1) stack.Pop();
                    pendingKey = null;
                    break;
                case TokenKind.String:
                    if (pendingKey is null) pendingKey = token;
                    else
                    {
                        stack.Peek().Values[pendingKey] = token!;
                        pendingKey = null;
                    }
                    break;
            }
        }
        return root;
    }

    public static VdfNode? TryParseFile(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private enum TokenKind { String, Open, Close, End }

    private static string? NextToken(string s, ref int i, out TokenKind kind)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            // Conditional tags like [$WIN32] after a value: skip them.
            if (c == '[')
            {
                while (i < s.Length && s[i] != ']' && s[i] != '\n') i++;
                if (i < s.Length) i++;
                continue;
            }
            if (c == '{') { i++; kind = TokenKind.Open; return null; }
            if (c == '}') { i++; kind = TokenKind.Close; return null; }

            var sb = new StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char n = s[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => n });
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                i++; // closing quote
            }
            else
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') sb.Append(s[i++]);
            }
            kind = TokenKind.String;
            return sb.ToString();
        }
        kind = TokenKind.End;
        return null;
    }
}
