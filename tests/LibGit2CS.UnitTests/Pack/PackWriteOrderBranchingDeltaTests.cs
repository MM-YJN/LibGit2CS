using System.Reflection;

using LibGit2CS.Pack;

namespace LibGit2CS.UnitTests.Pack;

/// <summary> Regression tests for <c>AddDescendantsToOrder</c> dropped the sibling-advance step after unwinding to a parent that has a sibling
/// (<c>pack-objects.c:496-498</c> — <c>po = po->delta_sibling</c>), so a branching delta chain at depth ≥ 2 (base R with children C1 (has child D) and C2) made
/// the write-order computation oscillate forever. </summary> <remarks> The algorithm is private static; the tests drive it via reflection on a hand-built
/// <c>PackObject</c> graph. A watchdog bounds the invocation so a non-terminating write-order computation fails the test instead of hanging it. </remarks>
public sealed class PackWriteOrderBranchingDeltaTests
{
    private static readonly Type s_packObjectType = typeof(GitPackWriter).GetNestedType("PackObject", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PackObject nested type not found");

    private static readonly FieldInfo s_deltaField = Field("Delta");
    private static readonly FieldInfo s_deltaChildField = Field("DeltaChild");
    private static readonly FieldInfo s_deltaSiblingField = Field("DeltaSibling");
    private static readonly FieldInfo s_filledField = Field("Filled");

    private static readonly MethodInfo s_addDescendants = typeof(GitPackWriter).GetMethod(
        "AddDescendantsToOrder", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AddDescendantsToOrder not found");

    private static FieldInfo Field(string name) => s_packObjectType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException($"PackObject.{name} not found");

    private static object NewPackObject() => Activator.CreateInstance(s_packObjectType, nonPublic: true)!;

    private static void Link(object child, object? parent, object? childSibling, bool setChildOnParent = true)
    {
        // parent.DeltaChild = child (first child), child.Delta = parent,
        // child.DeltaSibling = childSibling — mirrors the wiring built in
        // ComputeWriteOrderAsync (GitPackWriter.cs:823-833).
        s_deltaField.SetValue(child, parent);
        s_deltaSiblingField.SetValue(child, childSibling);
        if (parent is not null && setChildOnParent)
        {
            s_deltaChildField.SetValue(parent, child);
        }
    }

    private static List<object> RunAddDescendants(object root, int timeoutMs = 5000)
    {
        Type listType = typeof(List<>).MakeGenericType(s_packObjectType);
        object order = Activator.CreateInstance(listType)!;

        // A non-terminating write-order walk would loop forever; bound the invocation.
        Task task = Task.Run(() => s_addDescendants.Invoke(null, [order, root]));
        bool completed = task.Wait(TimeSpan.FromMilliseconds(timeoutMs));
        if (!completed)
        {
            throw new TimeoutException("AddDescendantsToOrder did not terminate (infinite loop)");
        }

        return ((IEnumerable<object>)order).ToList();
    }

    private static void AssertExactlyOnce(List<object> order, object po)
    {
        Assert.Single(order, o => ReferenceEquals(o, po));
    }

    [Fact]
    public void AddDescendants_BranchingChain_TerminatesAndVisitsAllOnce()
    {
        // repro: base R with children C1 (which has child D) and C2.
        object r = NewPackObject();
        object c1 = NewPackObject();
        object c2 = NewPackObject();
        object d = NewPackObject();

        Link(c1, r, c2);   // r.DeltaChild = c1, c1.DeltaSibling = c2
        Link(d, c1, null); // c1.DeltaChild = d
        Link(c2, r, null, setChildOnParent: false); // c2.Delta = r, sibling null

        List<object> order = RunAddDescendants(r);

        // Every object is visited exactly once: R, C1, C2, D.
        Assert.Equal(4, order.Count);
        AssertExactlyOnce(order, r);
        AssertExactlyOnce(order, c1);
        AssertExactlyOnce(order, c2);
        AssertExactlyOnce(order, d);

        // The root and its first child come first; the depth-2 leaf last.
        Assert.Same(r, order[0]);
        Assert.Same(d, order[3]);
    }

    [Fact]
    public void AddDescendants_DeeperBranching_TerminatesAndVisitsAllOnce()
    {
        // Three levels with branching at every level:
        // R -> A -> B -> X, Y; A's sibling C (child Z).
        object r = NewPackObject();
        object a = NewPackObject();
        object b = NewPackObject();
        object x = NewPackObject();
        object y = NewPackObject();
        object c = NewPackObject();
        object z = NewPackObject();

        Link(a, r, c);    // R's children: A (sibling C)
        Link(b, a, null); // A's children: B
        Link(x, b, y);    // B's children: X (sibling Y)
        Link(y, b, null, setChildOnParent: false); // Y's own Delta link
        Link(c, r, null, setChildOnParent: false); // C's sibling link only
        Link(z, c, null); // C's child Z

        List<object> order = RunAddDescendants(r);
        AssertExactlyOnce(order, r);
        AssertExactlyOnce(order, a);
        AssertExactlyOnce(order, b);
        AssertExactlyOnce(order, x);
        AssertExactlyOnce(order, y);
        AssertExactlyOnce(order, c);
        AssertExactlyOnce(order, z);
    }

    [Fact]
    public void AddDescendants_AlreadyFilled_IsSkipped()
    {
        // Objects already filled by an earlier family must not be re-added.
        // (AddToWriteOrder skips filled objects; the traversal itself still
        // walks the family — matching C, where the family entry is gated on
        // `!po->filled` in compute_write_order.)
        object r = NewPackObject();
        object c1 = NewPackObject();
        object c2 = NewPackObject();
        object d = NewPackObject();

        Link(c1, r, c2);
        Link(d, c1, null);
        Link(c2, r, null, setChildOnParent: false);

        s_filledField.SetValue(r, true);
        s_filledField.SetValue(c1, true);
        s_filledField.SetValue(c2, true);
        s_filledField.SetValue(d, true);

        List<object> order = RunAddDescendants(r);
        Assert.Empty(order);
    }
}
