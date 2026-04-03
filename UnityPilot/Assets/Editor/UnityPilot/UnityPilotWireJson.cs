using System;

namespace SkillEditor.Editor.UnityPilot
{
    /// <summary>
    /// 通信日志：从完整 JSON 中优先取出 payload；若无则退回原文（避免误伤嵌套字段）。
    /// </summary>
    internal static class UnityPilotWireJson
    {
        public static string StripEnvelopeForDisplay(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            json = json.Trim();
            if (!json.StartsWith("{", StringComparison.Ordinal))
                return json;

            var payload = TryExtractPayloadValue(json, out var hasPayloadKey);
            return hasPayloadKey ? payload : json;
        }

        private static string TryExtractPayloadValue(string json, out bool hasPayloadKey)
        {
            hasPayloadKey = false;
            var key = "\"payload\"";
            var i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
                return null;

            i = json.IndexOf(':', i + key.Length);
            if (i < 0)
                return null;

            i++;
            i = SkipWs(json, i);
            hasPayloadKey = true;
            if (i >= json.Length)
                return string.Empty;

            return ReadJsonValue(json, i, out _);
        }

        private static string ReadJsonValue(string s, int start, out int end)
        {
            start = SkipWs(s, start);
            end = start;
            if (start >= s.Length)
                return string.Empty;

            var c = s[start];
            if (c == '"')
                return ReadString(s, start, out end);
            if (c == '{')
                return ReadBracket(s, start, out end);
            if (c == '[')
                return ReadArray(s, start, out end);

            if (c == 't' && start + 3 < s.Length && s.Substring(start, 4) == "true")
            {
                end = start + 4;
                return "true";
            }

            if (c == 'f' && start + 4 < s.Length && s.Substring(start, 5) == "false")
            {
                end = start + 5;
                return "false";
            }

            if (c == 'n' && start + 3 < s.Length && s.Substring(start, 4) == "null")
            {
                end = start + 4;
                return "null";
            }

            if (c == '-' || char.IsDigit(c))
                return ReadNumber(s, start, out end);

            end = start;
            return string.Empty;
        }

        private static string ReadString(string s, int start, out int end)
        {
            end = start + 1;
            while (end < s.Length)
            {
                if (s[end] == '\\' && end + 1 < s.Length)
                {
                    end += 2;
                    continue;
                }

                if (s[end] == '"')
                {
                    end++;
                    break;
                }

                end++;
            }

            return s.Substring(start, end - start);
        }

        private static string ReadBracket(string s, int start, out int end)
        {
            var depth = 0;
            var inStr = false;
            var esc = false;
            for (var i = start; i < s.Length; i++)
            {
                var ch = s[i];
                if (inStr)
                {
                    if (esc)
                        esc = false;
                    else if (ch == '\\')
                        esc = true;
                    else if (ch == '"')
                        inStr = false;
                    continue;
                }

                if (ch == '"')
                {
                    inStr = true;
                    continue;
                }

                if (ch == '{')
                    depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i + 1;
                        return s.Substring(start, end - start);
                    }
                }
            }

            end = s.Length;
            return s.Substring(start);
        }

        private static string ReadArray(string s, int start, out int end)
        {
            var depth = 0;
            var inStr = false;
            var esc = false;
            for (var i = start; i < s.Length; i++)
            {
                var ch = s[i];
                if (inStr)
                {
                    if (esc)
                        esc = false;
                    else if (ch == '\\')
                        esc = true;
                    else if (ch == '"')
                        inStr = false;
                    continue;
                }

                if (ch == '"')
                {
                    inStr = true;
                    continue;
                }

                if (ch == '[')
                    depth++;
                else if (ch == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i + 1;
                        return s.Substring(start, end - start);
                    }
                }
            }

            end = s.Length;
            return s.Substring(start);
        }

        private static string ReadNumber(string s, int start, out int end)
        {
            var i = start;
            if (i < s.Length && s[i] == '-')
                i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-'))
                i++;
            end = i;
            return s.Substring(start, end - start);
        }

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
            return i;
        }
    }
}
