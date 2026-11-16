#nullable enable
using T3.Core.Utils;

namespace Lib.io.web.dom;

[Guid("1122303e-2026-43ab-b0e8-d39e36e0ebde")]
public sealed class ElementVisibility : Instance<ElementVisibility>
{
    [Output(Guid = "5f0151ba-9fe5-4488-a415-80736542bcc8", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public ElementVisibility()
    {
        Output.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var selector = Selector.GetValue(context)?.Trim();
        if (string.IsNullOrEmpty(selector))
            return;

        var visible = Visible.GetValue(context);
        if (HideMode.GetEnumValue<Modes>(context) == Modes.Display)
            WebDomOps.Add(context, WebDomOps.Style, selector, "display", visible ? null : "none");
        else
            WebDomOps.Add(context, WebDomOps.Style, selector, "visibility", visible ? null : "hidden");
    }

    private enum Modes
    {
        Visibility,
        Display,
    }

    [Input(Guid = "35c88543-9628-47a9-9ed3-4c55262e4d8a")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "b63ecc86-1449-4876-a61d-f19c1346b887")]
    public readonly InputSlot<bool> Visible = new();

    [Input(Guid = "5c9c63b6-1eef-4152-a504-5d3fcebf8bd9", MappedType = typeof(Modes))]
    public readonly InputSlot<int> HideMode = new();
}
