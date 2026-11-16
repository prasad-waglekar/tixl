#nullable enable
namespace Lib.io.web;

/// <summary>One DOM change: set (or, with a null value, remove) a style property or an attribute on all elements matching a CSS selector.</summary>
internal readonly record struct DomOp(char Kind, string Selector, string Name, string? Value)
{
    public (char, string, string) Key => (Kind, Selector, Name);
}

/// <summary>A one-shot JavaScript call: window[Function](Argument). Not diffed; every AddCall becomes one invocation.</summary>
internal readonly record struct DomCall(string Function, string Argument);

/// <summary>
/// Collects the DOM changes of the modifier operators (CssProperty, ElementAttribute, ...) during one evaluation of an
/// HtmlRenderer. The renderer puts an instance into <c>EvaluationContext.ObjectVariables</c> while it evaluates its
/// Modifiers input; operators outside of a renderer find no scope and do nothing.
/// </summary>
internal sealed class WebDomOps
{
    public const string ContextKey = "Lib.io.web.DomOps";
    public const char Style = 's';
    public const char Attribute = 'a';
    public const char Text = 't';

    public readonly List<DomOp> Ops = new();
    public readonly List<DomCall> Calls = new();

    public static void Add(EvaluationContext context, char kind, string selector, string name, string? value)
    {
        if (context.ObjectVariables.TryGetValue(ContextKey, out var scope) && scope is WebDomOps ops)
            ops.Ops.Add(new DomOp(kind, selector, name, value));
    }

    public static void AddCall(EvaluationContext context, string function, string argument)
    {
        if (context.ObjectVariables.TryGetValue(ContextKey, out var scope) && scope is WebDomOps ops)
            ops.Calls.Add(new DomCall(function, argument));
    }
}
