using System;
using System.Collections.Generic;

namespace BetterGhPanel.Core
{
    /// <summary>A small immutable expression, parsed once when its setting changes.</summary>
    public sealed class PanelFormula
    {
        public const int MaximumLength = 200;
        private readonly Func<double, double> _evaluate;
        private PanelFormula(string text, Func<double, double> evaluate) { Text = text; _evaluate = evaluate; }
        public string Text { get; }
        public string Pretty
        {
            get
            {
                var text = Text.ToCharArray();
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == '-' && (i == 0 || text[i - 1] != 'e' && text[i - 1] != 'E')) text[i] = '−';
                return new string(text);
            }
        }
        public double Evaluate(double x) => double.IsNaN(x) || double.IsInfinity(x) ? double.NaN : _evaluate(x);

        public static bool TryCompile(string? text, out PanelFormula? formula, out string error)
        {
            formula = null;
            error = "";
            string normalized = (text ?? "").Trim().Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace('–', '-');
            if (normalized.Length > 0 && (normalized[0] == '+' || normalized[0] == '-') &&
                TryParse(normalized, out formula, out error)) return true;
            if (normalized.Length > 0 && "+-*/^".IndexOf(normalized[0]) >= 0) normalized = "x" + normalized;
            return TryParse(normalized, out formula, out error);
        }

        public static PanelFormula? NormalizeSetting(PanelFormula? formula) =>
            string.Equals(formula?.Text, "x", StringComparison.OrdinalIgnoreCase) ? null : formula;

        private static bool TryParse(string normalized, out PanelFormula? formula, out string error)
        {
            formula = null;
            error = "";
            if (normalized.Length > MaximumLength) { error = "Keep the formula to 200 characters"; return false; }
            try
            {
                var parser = new Parser(normalized);
                var evaluate = parser.Parse();
                formula = new PanelFormula(normalized, evaluate);
                return true;
            }
            catch (FormatException ex) { error = ex.Message; return false; }
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _position, _depth;
            private bool _usesX;
            internal Parser(string text) { _text = text; }
            private char Peek { get { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; return _position < _text.Length ? _text[_position] : '\0'; } }
            private bool Take(char c) { if (Peek != c) return false; _position++; return true; }
            private static FormatException Error(string message) => new FormatException(message);
            internal Func<double, double> Parse()
            {
                if (Peek == '\0') throw Error("Use x for the shown number, e.g. x*2");
                var expression = Expression();
                if (Peek == ')') throw Error("Extra )");
                if (Peek == ',') throw Error("Use . for decimals (1.5, not 1,5)");
                if (char.IsLetter(Peek))
                {
                    string name = Name(), key = name.ToLowerInvariant();
                    throw Error(key == "x" || key == "pi" || Arity(key) > 0 ? "Use * between numbers and x (2*x)" :
                        "Leave out units like '" + name + "'. x is already in the shown unit");
                }
                if (Peek != '\0') throw Error("Unexpected '" + Peek + "'");
                if (!_usesX) throw Error("Use x for the shown number, e.g. x*2");
                return expression;
            }
            private Func<double, double> Expression()
            {
                var left = Term();
                while (Peek == '+' || Peek == '-')
                { char op = _text[_position++]; var a = left; var b = Term(); left = op == '+' ? x => a(x) + b(x) : x => a(x) - b(x); }
                return left;
            }
            private Func<double, double> Term()
            {
                var left = Unary();
                while (Peek == '*' || Peek == '/')
                { char op = _text[_position++]; var a = left; var b = Unary(); left = op == '*' ? x => a(x) * b(x) : x => a(x) / b(x); }
                return left;
            }
            private Func<double, double> Unary()
            {
                if (++_depth > 32) throw Error("Formula is nested too deeply");
                try
                {
                    if (Take('-')) { var value = Unary(); return x => -value(x); }
                    if (Take('+')) return Unary();
                    var basis = Primary();
                    if (!Take('^')) return basis;
                    var exponent = Unary();
                    return x => Math.Pow(basis(x), exponent(x));
                }
                finally { _depth--; }
            }
            private string Name()
            {
                int start = _position;
                while (_position < _text.Length && (char.IsLetter(_text[_position]) || _text[_position] == '_' || _text[_position] == '²' || _text[_position] == '³')) _position++;
                return _text.Substring(start, _position - start);
            }
            private static int Arity(string key) => key == "min" || key == "max" ? 2 :
                key == "abs" || key == "sqrt" || key == "floor" || key == "ceil" ? 1 : 0;
            private Func<double, double> Primary()
            {
                if (Take('(')) { var value = Expression(); if (!Take(')')) throw Error("Missing )"); return value; }
                char c = Peek;
                if (char.IsDigit(c) || c == '.')
                {
                    int start = _position;
                    while (char.IsDigit(PeekNumber)) _position++;
                    if (PeekNumber == '.') { _position++; while (char.IsDigit(PeekNumber)) _position++; }
                    if (PeekNumber == 'e' || PeekNumber == 'E')
                    { _position++; if (PeekNumber == '+' || PeekNumber == '-') _position++; while (char.IsDigit(PeekNumber)) _position++; }
                    if (!PanelUnits.TryParseOperand(_text.Substring(start, _position - start), out double value)) throw Error("Use a number like 1.5 or 2e3");
                    return _ => value;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    string name = Name(), key = name.ToLowerInvariant();
                    if (key == "x") { _usesX = true; return x => x; }
                    if (key == "pi") return _ => Math.PI;
                    int count = Arity(key);
                    if (count == 0) throw Error("Unknown name '" + name + "'. Use x");
                    string arity = name + " takes " + count + " value" + (count == 1 ? "" : "s");
                    if (!Take('(')) throw Error(arity);
                    var args = new List<Func<double, double>>();
                    if (Peek != ')') { args.Add(Expression()); while (Take(',')) { if (Peek == ')') throw Error(arity); args.Add(Expression()); } }
                    if (!Take(')')) throw Error("Missing )");
                    if (args.Count != count) throw Error(arity);
                    var a = args[0];
                    switch (key)
                    {
                        case "abs": return x => Math.Abs(a(x));
                        case "sqrt": return x => Math.Sqrt(a(x));
                        case "floor": return x => Math.Floor(a(x));
                        case "ceil": return x => Math.Ceiling(a(x));
                        case "min": return x => Math.Min(a(x), args[1](x));
                        default: return x => Math.Max(a(x), args[1](x));
                    }
                }
                if (c == '\0') throw Error("Formula ends early");
                if (c == ')') throw Error("Extra )");
                if (c == ',') throw Error("Use . for decimals (1.5, not 1,5)");
                throw Error("Unexpected '" + c + "'");
            }
            private char PeekNumber => _position < _text.Length ? _text[_position] : '\0';
        }
    }
}
