using System.Globalization;
using System.Text;

namespace DiagnosticDamageProbe;

// Small flat JSON writer. Names are escaped; numbers use invariant, round-trip formatting.
internal sealed class LogFields
{
    private readonly StringBuilder _text = new StringBuilder("{");
    private bool _hasFields;

    internal LogFields Text(string key, string value)
    {
        Key(key);
        if (value == null) _text.Append("null");
        else Quote(value);
        return this;
    }

    internal LogFields Number(string key, float value)
    {
        Key(key);
        if (float.IsNaN(value) || float.IsInfinity(value)) Quote(value.ToString(CultureInfo.InvariantCulture));
        else _text.Append(value.ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    internal LogFields Flag(string key, bool value)
    {
        Key(key);
        _text.Append(value ? "true" : "false");
        return this;
    }

    public override string ToString() => _text.ToString() + "}";

    private void Key(string key)
    {
        if (_hasFields) _text.Append(',');
        _hasFields = true;
        Quote(key);
        _text.Append(':');
    }

    private void Quote(string value)
    {
        _text.Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') _text.Append('\\').Append(c);
            else if (char.IsControl(c)) _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else _text.Append(c);
        }
        _text.Append('"');
    }
}
