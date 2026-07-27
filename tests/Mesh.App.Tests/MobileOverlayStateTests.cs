using Mesh.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mesh.App.Tests;

[TestClass]
public sealed class MobileOverlayStateTests
{
    [TestMethod]
    public void NestedScopes_KeepOverlayOpenUntilLastScopeCloses()
    {
        var state = new MobileOverlayState();
        var changes = 0;
        state.Changed += () => changes++;

        var first = state.Enter();
        var second = state.Enter();

        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, changes);

        first.Dispose();

        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, changes);

        second.Dispose();

        Assert.IsFalse(state.IsOpen);
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void ScopeDisposal_IsIdempotent()
    {
        var state = new MobileOverlayState();
        var scope = state.Enter();

        scope.Dispose();
        scope.Dispose();

        Assert.IsFalse(state.IsOpen);
    }
}
