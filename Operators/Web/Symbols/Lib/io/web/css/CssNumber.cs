#nullable enable
using System.Globalization;

namespace Lib.io.web.css;

[Guid("d1081486-7cf6-4f77-9555-a836a2f6e5fe")]
public sealed class CssNumber : Instance<CssNumber>
{
    [Output(Guid = "54533ad0-d2de-4e89-88e4-acdc99dafc15", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public CssNumber()
    {
        Output.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var selector = Selector.GetValue(context)?.Trim();
        var property = Property.GetValue(context)?.Trim();
        if (string.IsNullOrEmpty(selector) || string.IsNullOrEmpty(property))
            return;

        var value = Value.GetValue(context);
        if (!float.IsFinite(value))
            return;

        var unit = Unit.GetValue(context)?.Trim() ?? "";
        if (_text == null || value != _lastValue || unit != _lastUnit)
        {
            _text = value.ToString("0.######", CultureInfo.InvariantCulture) + unit; // only formatted when the number changes
            _lastValue = value;
            _lastUnit = unit;
        }

        WebDomOps.Add(context, WebDomOps.Style, selector, property, _text);
    }

    private string? _text;
    private float _lastValue;
    private string _lastUnit = "";

    [Input(Guid = "1e4f379a-ed8f-463f-b27f-eef169946af8")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "4f6e23e9-d4cf-4b7b-8f17-b53eb1f13822")]
    public readonly InputSlot<string> Property = new();

    [Input(Guid = "8e35ad3c-4447-451c-b163-4511e9e27e3e")]
    public readonly InputSlot<float> Value = new();

    [Input(Guid = "0a79f4fb-f272-407b-882a-4d5922c1e875")]
    public readonly InputSlot<string> Unit = new();
}
