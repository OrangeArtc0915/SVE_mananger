using System.IO;
using System.Text;

namespace StardewLauncher.Core.Games;

/// <summary>
/// Valve KeyValues（.vdf）格式的轻量解析器。
/// 只处理「带引号的键值 + 花括号嵌套」这一种结构，足够读取 Steam 的库配置。
/// </summary>
public static class VdfReader
{
    public static Dictionary<string, object>? ParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? ParseString(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, object> ParseString(string text)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Dictionary<string, object>>();
        stack.Push(root);

        var index = 0;
        string? pendingKey = null;

        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            // 跳过 // 行注释
            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }

            if (current == '{')
            {
                if (pendingKey is not null)
                {
                    var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    stack.Peek()[pendingKey] = child;
                    stack.Push(child);
                    pendingKey = null;
                }

                index++;
                continue;
            }

            if (current == '}')
            {
                if (stack.Count > 1) stack.Pop();
                index++;
                continue;
            }

            if (current != '"')
            {
                index++;
                continue;
            }

            var token = ReadQuoted(text, ref index);

            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek()[pendingKey] = token;
                pendingKey = null;
            }
        }

        return root;
    }

    /// <summary>读取一个带引号的字符串，处理 \" 与 \\ 转义，并把 \\ 还原为单斜杠。</summary>
    private static string ReadQuoted(string text, ref int index)
    {
        var builder = new StringBuilder();
        index++; // 跳过起始引号

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                builder.Append(next == 'n' ? '\n' : next);
                index += 2;
                continue;
            }

            if (current == '"')
            {
                index++;
                break;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }
}
