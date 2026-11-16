#nullable enable
using System.Globalization;

namespace Lib.io.web.css;

[Guid("9c72df6b-c56f-46b3-836d-5f14a6cf1b01")]
public sealed class CssColor : Instance<CssColor>
{
    [Output(Guid = "fd00b7f6-f443-4ea7-93d1-00bbceb24e5d", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public CssColor()
    {
        Output.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var selector = Selector.GetValue(context)?.Trim();
        var property = Property.GetValue(context)?.Trim();
        if (string.IsNullOrEmpty(selector) || string.IsNullOrEmpty(property))
            return;

        var color = Color.GetValue(context);
        if (_text == null || color != _lastColor)
        {
            _text = "rgba(" + To255(color.X) + "," + To255(color.Y) + "," + To255(color.Z) + ","
                    + Math.Clamp(color.W, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture) + ")";
            _lastColor = color;
        }

        WebDomOps.Add(context, WebDomOps.Style, selector, property, _text);
    }

    private static string To255(float channel)
        => ((int)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f)).ToString(CultureInfo.InvariantCulture);

    private string? _text;
    private Vector4 _lastColor;

    [Input(Guid = "3494989d-93a5-4e21-a01d-5fdcd94df26f")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "b4b79780-5931-4a6f-8711-e45ae32d96f4")]
    public readonly InputSlot<string> Property = new();

    [Input(Guid = "90c629c9-a3e1-47a8-a5f6-f56de6a48300")]
    public readonly InputSlot<Vector4> Color = new();
}
