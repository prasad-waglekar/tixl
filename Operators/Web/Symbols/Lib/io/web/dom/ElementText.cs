#nullable enable
namespace Lib.io.web.dom;

[Guid("7a3e9b1d-4c52-4e01-9b8a-2f1d6e3a5c7f")]
public sealed class ElementText : Instance<ElementText>
{
    [Output(Guid = "8b2f4c6e-d1a3-4b5c-9d7e-1f3a5b7c9d1e", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public ElementText() { Output.UpdateAction += Update; }

    private void Update(EvaluationContext context)
    {
        var selector = Selector.GetValue(context)?.Trim();
        if (string.IsNullOrEmpty(selector)) return;
        WebDomOps.Add(context, WebDomOps.Text, selector, "", Text.GetValue(context) ?? "");
    }

    [Input(Guid = "1c4e6a8b-2d5f-4a7c-9e1b-3d5f7a9c1e2b")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "2d5f7a9b-3e6a-4b8c-a0d2-4e6a8b0c2d3e")]
    public readonly InputSlot<string> Text = new();
}
