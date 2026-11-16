#nullable enable
namespace Lib.io.web.js;

[Guid("4f8b2d1e-7a3c-4e5b-9d6a-1b3d5f7a9c2e")]
public sealed class CallJavaScript : Instance<CallJavaScript>
{
    [Output(Guid = "5a1e3c7d-9b4f-4a2c-8e6b-0d2f4a6c8e1b", DirtyFlagTrigger = DirtyFlagTrigger.Always)]
    public readonly Slot<Command> Output = new();

    public CallJavaScript() { Output.UpdateAction += Update; }

    private void Update(EvaluationContext context)
    {
        var fn = Function.GetValue(context)?.Trim();
        if (string.IsNullOrEmpty(fn)) { _lastFunction = null; _lastArgument = null; return; }
        var arg = Argument.GetValue(context) ?? "";
        if (fn == _lastFunction && arg == _lastArgument) return;
        WebDomOps.AddCall(context, fn, arg);
        _lastFunction = fn;
        _lastArgument = arg;
    }

    private string? _lastFunction;
    private string? _lastArgument;

    [Input(Guid = "6b2f4a8c-0c5a-4b3d-9f7c-1e3a5b7d9f2c")]
    public readonly InputSlot<string> Function = new();

    [Input(Guid = "7c3a5b9d-1d6b-4c4e-a0d8-2f4a6c8e0a3d")]
    public readonly InputSlot<string> Argument = new();
}
