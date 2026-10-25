using System;
using System.Collections.Generic;

namespace legado_wp.Core
{
    /// <summary>
    /// 规则切分（对照 Kotlin RuleAnalyzer.kt 的 splitRule 状态机）。
    /// 按 && / || / %% 切分规则，跳过 [] () 平衡组内的分隔符。
    /// </summary>
    public class RuleAnalyzer
    {
        private readonly string _queue;
        private int _pos;
        private int _start;
        private int _startX;
        private readonly List<string> _rule = new List<string>();
        private int _step;
        public string ElementsType = "";

        public RuleAnalyzer(string data)
        {
            _queue = data ?? "";
        }

        public void Trim()
        {
            if (_pos >= _queue.Length) return;
            if (_queue[_pos] == '@' || _queue[_pos] < '!')
            {
                _pos++;
                while (_pos < _queue.Length && (_queue[_pos] == '@' || _queue[_pos] < '!')) _pos++;
                _start = _pos;
                _startX = _pos;
            }
        }

        private bool ConsumeTo(string seq)
        {
            _start = _pos;
            var offset = _queue.IndexOf(seq, _pos, StringComparison.Ordinal);
            if (offset != -1)
            {
                _pos = offset;
                return true;
            }
            return false;
        }

        private bool ConsumeToAny(params string[] seq)
        {
            var pos = _pos;
            while (pos != _queue.Length)
            {
                foreach (var s in seq)
                {
                    if (pos + s.Length <= _queue.Length &&
                        string.CompareOrdinal(_queue, pos, s, 0, s.Length) == 0)
                    {
                        _step = s.Length;
                        _pos = pos;
                        return true;
                    }
                }
                pos++;
            }
            return false;
        }

        private int FindToAny(params char[] seq)
        {
            var pos = _pos;
            while (pos != _queue.Length)
            {
                foreach (var s in seq) if (_queue[pos] == s) return pos;
                pos++;
            }
            return -1;
        }

        /// <summary>规则平衡组（引号内不转义，对照 chompRuleBalanced）。</summary>
        private bool ChompBalanced(char open, char close)
        {
            int depth = 0, i = _pos;
            bool inQuote = false; char quote = '"';
            while (i < _queue.Length)
            {
                var c = _queue[i];
                if (inQuote)
                {
                    if (c == quote) inQuote = false;
                }
                else if (c == '"' || c == '\'')
                {
                    inQuote = true; quote = c;
                }
                else if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) { _pos = i; return true; }
                }
                i++;
            }
            return false;
        }

        public List<string> SplitRule(params string[] split)
        {
            if (split.Length == 1)
            {
                ElementsType = split[0];
                if (!ConsumeTo(ElementsType))
                {
                    _rule.Add(_queue.Substring(_startX));
                    return _rule;
                }
                _step = ElementsType.Length;
                return SplitRuleNext();
            }
            if (!ConsumeToAny(split))
            {
                _rule.Add(_queue.Substring(_startX));
                return _rule;
            }

            var end = _pos;
            _pos = _start;
            while (true)
            {
                var st = FindToAny('[', '(');
                if (st == -1)
                {
                    _rule.Add(_queue.Substring(_startX, end - _startX));
                    ElementsType = _queue.Substring(end, _step);
                    _pos = end + _step;
                    while (ConsumeTo(ElementsType))
                    {
                        _rule.Add(_queue.Substring(_start, _pos - _start));
                        _pos += _step;
                    }
                    _rule.Add(_queue.Substring(_pos));
                    return _rule;
                }
                if (st > end)
                {
                    _rule.Add(_queue.Substring(_startX, end - _startX));
                    ElementsType = _queue.Substring(end, _step);
                    _pos = end + _step;
                    while (ConsumeTo(ElementsType) && _pos < st)
                    {
                        _rule.Add(_queue.Substring(_start, _pos - _start));
                        _pos += _step;
                    }
                    if (_pos > st)
                    {
                        _startX = _start;
                        return SplitRuleNext();
                    }
                    _rule.Add(_queue.Substring(_pos));
                    return _rule;
                }
                _pos = st;
                var next = _queue[_pos] == '[' ? ']' : ')';
                if (!ChompBalanced(_queue[_pos], next))
                    throw new Exception(_queue.Substring(0, _start) + "后未平衡");
                if (end <= _pos) break;
            }
            _start = _pos;
            return SplitRule(split);
        }

        private List<string> SplitRuleNext()
        {
            var end = _pos;
            _pos = _start;
            while (true)
            {
                var st = FindToAny('[', '(');
                if (st == -1)
                {
                    _rule.Add(_queue.Substring(_startX, end - _startX));
                    _pos = end + _step;
                    while (ConsumeTo(ElementsType))
                    {
                        _rule.Add(_queue.Substring(_start, _pos - _start));
                        _pos += _step;
                    }
                    _rule.Add(_queue.Substring(_pos));
                    return _rule;
                }
                if (st > end)
                {
                    _rule.Add(_queue.Substring(_startX, end - _startX));
                    _pos = end + _step;
                    while (ConsumeTo(ElementsType) && _pos < st)
                    {
                        _rule.Add(_queue.Substring(_start, _pos - _start));
                        _pos += _step;
                    }
                    if (_pos > st)
                    {
                        _startX = _start;
                        return SplitRuleNext();
                    }
                    _rule.Add(_queue.Substring(_pos));
                    return _rule;
                }
                _pos = st;
                var next = _queue[_pos] == '[' ? ']' : ')';
                if (!ChompBalanced(_queue[_pos], next))
                    throw new Exception(_queue.Substring(0, _start) + "后未平衡");
                if (end <= _pos) break;
            }
            _start = _pos;
            if (!ConsumeTo(ElementsType))
            {
                _rule.Add(_queue.Substring(_startX));
                return _rule;
            }
            return SplitRuleNext();
        }
    }

    /// <summary>结果合并（对照 Kotlin RuleCombiner.kt）。</summary>
    public static class RuleCombiner
    {
        public static void CombineResults<T>(List<List<T>> results, string elementsType, List<T> target)
        {
            if (results.Count == 0) return;
            if (elementsType == "%%")
            {
                for (var i = 0; i < results[0].Count; i++)
                    foreach (var temp in results)
                        if (i < temp.Count) target.Add(temp[i]);
            }
            else
            {
                foreach (var temp in results) target.AddRange(temp);
            }
        }
    }
}
