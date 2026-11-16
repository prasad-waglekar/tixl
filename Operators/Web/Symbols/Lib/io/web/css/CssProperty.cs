#nullable enable
namespace Lib.io.web.css;

[Guid("8bb1eeb2-b94a-48c5-a1c4-8ee265709e97")]
public sealed class CssProperty : Instance<CssProperty>
{
    [Output(Guid = "2c2d59c3-bce5-4296-b95e-3931e94d1627", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public CssProperty()
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
        WebDomOps.Add(context, WebDomOps.Style, selector, property, string.IsNullOrEmpty(value) ? null : value);
    }

    [Input(Guid = "749a77f0-f108-47b2-9fca-f3fa3ad15a39")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "f5a475a0-3f89-496a-b6fd-0bd19a9247db")]
    public readonly InputSlot<string> Property = new();

    [Input(Guid = "0ed720d0-72a1-4fe3-b2ed-9786938fd163")]
    public readonly InputSlot<string> Value = new();
}
