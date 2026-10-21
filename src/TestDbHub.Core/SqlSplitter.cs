using System.Text;

namespace TestDbHub.Core;

public static class SqlSplitter
{
    /// <summary>
    /// Split a script into statements, ignoring semicolons inside quotes and
    /// comments. Good enough for hand-written test scripts and dumps.
    /// </summary>
    public static IReadOnlyList<string> Split(string? script)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(script)) return result;
        var cur = new StringBuilder();
        int i = 0, n = script.Length;

        void Flush()
        {
            var s = cur.ToString().Trim();
            if (s.Length > 0) result.Add(s);
            cur.Clear();
        }

        while (i < n)
        {
            char c = script[i];
            char next = i + 1 < n ? script[i + 1] : '\0';
            if ((c == '-' && next == '-') || c == '#')
            {
                int end = script.IndexOf('\n', i);
                i = end < 0 ? n : end + 1;
                cur.Append('\n');
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                cur.Append(' ');
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                int j = i + 1;
                while (j < n)
                {
                    if (script[j] == '\\' && c != '`') { j += 2; continue; }
                    if (script[j] == c)
                    {
                        if (j + 1 < n && script[j + 1] == c) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                int stop = Math.Min(j + 1, n);
                cur.Append(script, i, stop - i);
                i = stop;
                continue;
            }
            if (c == ';')
            {
                Flush();
                i++;
                continue;
            }
            cur.Append(c);
            i++;
        }
        Flush();
        return result;
    }
}
