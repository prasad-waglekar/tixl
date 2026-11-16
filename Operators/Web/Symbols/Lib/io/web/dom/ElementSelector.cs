#nullable enable
using System.Globalization;
using T3.Core.Utils;

namespace Lib.io.web.dom;

[Guid("a3edfbcd-e96c-4bac-be9e-0180753ec2e9")]
public sealed class ElementSelector : Instance<ElementSelector>
{
    [Output(Guid = "1f6f89de-446a-4556-a89f-b84333307d41")]
    public readonly Slot<string> Result = new();

    public ElementSelector()
    {
        Result.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        var parent = ParentSelector.GetValue(context)?.Trim() ?? "";
        var selector = Selector.GetValue(context)?.Trim() ?? "";
        var relation = Relation.GetEnumValue<Relations>(context);
        var nth = Nth.GetValue(context);

        if (selector.Length == 0)
        {
            Result.Value = parent; // nothing to refine: pass the parent through
            return;
        }

        var part = Group(selector);
        if (nth > 0)
            part += ":nth-of-type(" + nth.ToString(CultureInfo.InvariantCulture) + ")";

        if (parent.Length == 0)
        {
            Result.Value = part;
            return;
        }

        if (relation == Relations.SameElement)
        {
            // A type selector must come first in a compound selector (path.active, not .activepath).
            Result.Value = char.IsLetter(part[0]) || part[0] == '*' ? part + Group(parent) : Group(parent) + part;
            return;
        }

        var separator = relation switch
                            {
                                Relations.DirectChild        => " > ",
                                Relations.NextSibling        => " + ",
                                Relations.SubsequentSibling  => " ~ ",
                                _                            => " "
                            };
        Result.Value = Group(parent) + separator + part;
    }

    // A selector list ("a, b") has to be grouped, otherwise the comma would split the combined selector.
    private static string Group(string selector) => selector.Contains(',') ? ":is(" + selector + ")" : selector;

    private enum Relations
    {
        Descendant,
        DirectChild,
        SameElement,
        NextSibling,
        SubsequentSibling,
    }

    [Input(Guid = "ff1fe235-0cf0-44d4-b826-cf2a16dc06f6")]
    public readonly InputSlot<string> ParentSelector = new();

    [Input(Guid = "37a0891c-642a-4162-817b-ae89941d0156")]
    public readonly InputSlot<string> Selector = new();

    [Input(Guid = "5e512a6a-8982-4d3e-8d23-3717230f88e3", MappedType = typeof(Relations))]
    public readonly InputSlot<int> Relation = new();

    [Input(Guid = "89b77332-0f2e-4eab-913d-7e5295ca7e08")]
    public readonly InputSlot<int> Nth = new();
}
