namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Direct unit coverage for <see cref="RunAsyncFailureTestEmitter.Emit"/>'s
/// <c>usesLookupPreload</c> branch -- the real bug this covers (a Lookup-bearing package's
/// generated <c>RunAsync_RollsBackAndNotifiesFailure_WhenTheRunCannotStart</c> test threw a real,
/// uncaught <c>SqlException</c> instead of asserting, because the Lookup reference-table preload
/// ran BEFORE <c>RunAsync</c>'s own try/catch and always attempts a real connection
/// <c>PackageHarness</c> can never fake) was found running <c>Package_Transforms.Tests</c> (the
/// first Lookup-bearing package this project ever generated tests for) end-to-end, not by a unit
/// test -- this file exists so the fix has direct coverage too, and so the
/// <c>usesLookupPreload &amp;&amp; hasFailureHandlers</c> combination (unevidenced in the tracked
/// portfolio: the one real Lookup package has no failure handlers and vice versa) is proven at
/// the text level even without a dedicated fixture, the same "mechanical parity" tier
/// <see cref="ComponentTestEmitterTests"/> already uses.
/// </summary>
public class RunAsyncFailureTestEmitterTests
{
    [Fact]
    public void Emit_AssertsBeginNeverCalled_WhenThePackageUsesLookupPreload()
    {
        var file = RunAsyncFailureTestEmitter.Emit(
            "MyPackage", "MyPackagePackage", hasFailureHandlers: false, supportsHappyPath: false, usesLookupPreload: true,
            includeNotifications: true);

        // No fault is injected -- the real, unfakeable Lookup connectivity failure is the fault.
        Assert.DoesNotContain("harness.ThrowOnGetBindToken =", file.Content);
        Assert.Contains("Assert.False(uow.BeginCalled);", file.Content);
        Assert.Contains("Assert.False(uow.RollbackCalled);", file.Content);
        Assert.Contains("Assert.False(uow.CommitCalled);", file.Content);
        Assert.Contains("Assert.False(harness.Notifier.Result!.Succeeded);", file.Content);
        Assert.DoesNotContain("FailureHandlersRun", file.Content);
    }

    [Fact]
    public void Emit_AssertsFailureHandlersStillRun_WhenThePackageUsesLookupPreloadAndDeclaresHandlers()
    {
        var file = RunAsyncFailureTestEmitter.Emit(
            "MyPackage", "MyPackagePackage", hasFailureHandlers: true, supportsHappyPath: false, usesLookupPreload: true,
            includeNotifications: true);

        Assert.Contains("Assert.False(uow.BeginCalled);", file.Content);
        Assert.Contains("Assert.False(uow.RollbackCalled);", file.Content);
        // A handler is still attempted even though no transaction was ever opened -- see
        // FakeUnitOfWork's own _transactionActive precondition (false when BeginAsync never ran).
        Assert.Contains("Assert.NotEmpty(uow.ExecutedSqlWithoutTransaction);", file.Content);
        Assert.Contains("Assert.NotEmpty(harness.Notifier.Result!.FailureHandlersRun);", file.Content);
    }

    [Fact]
    public void Emit_InjectsTheBindTokenFault_WhenThePackageDoesNotUseLookupPreload()
    {
        var file = RunAsyncFailureTestEmitter.Emit(
            "MyPackage", "MyPackagePackage", hasFailureHandlers: false, supportsHappyPath: false, usesLookupPreload: false);

        Assert.Contains("harness.ThrowOnGetBindToken = new InvalidOperationException", file.Content);
        Assert.Contains("Assert.True(uow.RollbackCalled);", file.Content);
        Assert.DoesNotContain("Assert.False(uow.BeginCalled);", file.Content);
    }

    [Fact]
    public void Emit_StillEmitsTheHappyPathTest_WhenSupported_RegardlessOfLookupPreload()
    {
        var file = RunAsyncFailureTestEmitter.Emit(
            "MyPackage", "MyPackagePackage", hasFailureHandlers: false, supportsHappyPath: true, usesLookupPreload: false);

        Assert.Contains("RunAsync_Succeeds_AgainstFakesWithNoFaultInjected", file.Content);
    }
}
