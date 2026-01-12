// 本文件提供 UTF-8 长度计算与安全截断，用于聊天发送的长度控制。
using System.Text;

namespace MitigationPolice.Chat;

public static class Utf8Util {
    public static int GetByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    public static string Truncate(string text, int maxBytes) {
        if (maxBytes <= 0) {
            return string.Empty;
        }

        if (GetByteCount(text) <= maxBytes) {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var used = 0;
        foreach (var rune in text.EnumerateRunes()) {
            var next = rune.Utf8SequenceLength;
            if (used + next > maxBytes) {
                break;
            }

            sb.Append(rune.ToString());
            used += next;
        }

        return sb.ToString();
    }
}

