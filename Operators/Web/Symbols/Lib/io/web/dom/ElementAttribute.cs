#nullable enable
namespace Lib.io.web.dom;

[Guid("e4500aed-7371-4c52-9a77-436a21362203")]
public sealed class ElementAttribute : Instance<ElementAttribute>
{
    [Output(Guid = "4d460ce2-e3d7-435d-9178-26208d7b51a9", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public ElementAttribute()
    {
        Output.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var selector = Selector.GetValue(context)?.Trim();
        var name = AttributeName.GetValue(context)?.Trim();
        if (string.IsNullOrEmpty(selector) || string.IsNullOrEmpty(name))
            return;

        var value = Remove.GetValue(context) ? null : Value.GetValue(context) ?? "";
        WebDomOps.Add(context, WebDomOps.Attribute, selector, name, value);
    }

    [Input(Guid = "63da7176-64ac-4560-b7ab-88ebb88d4bdb")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "a7d71ef8-6b31-45b2-8408-7140972c20bc")]
    public readonly InputSlot<string> AttributeName = new();

    [Input(Guid = "d9b8b567-28a6-4ef0-a172-418615392c59")]
    public readonly InputSlot<string> Value = new();

    [Input(Guid = "be59651d-79cb-4e0b-9081-5adb2b4a7c27")]
    public readonly InputSlot<bool> Remove = new();
}
