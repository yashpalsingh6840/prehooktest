namespace Etl.Core.Abstractions;

/// <summary>
/// Everything a ported SSIS Script Task is given. One parameter rather than four, specifically so
/// that adding a capability later does not force every already-written, already-reviewed Script
/// Task fill to be edited -- these are hand-written files with provenance, not regenerable output.
///
/// The mapping from what a real Script Task reaches for, to what is here:
///
/// | SSIS Script Task uses | Here |
/// |---|---|
/// | <c>Dts.Connections[...].AcquireConnection</c> for SQL | <see cref="Uow"/> (the package's one shared connection/transaction) |
/// | <c>Dts.Variables["System::PackageName"]</c> | <see cref="Load"/> |
/// | <c>Dts.Variables["User::Whatever"]</c> | <see cref="Variables"/> |
/// | <c>Dts.Connections[...].ConnectionString</c> for a file path, or anything else configured | <see cref="Services"/> |
/// | <c>Dts.Events.FireError</c> + <c>Dts.TaskResult = Failure</c> | throw |
/// | <c>Dts.Events.FireInformation</c> | an <c>ILogger</c> resolved from <see cref="Services"/> |
///
/// <see cref="Services"/> is the loose one, and it is loose on purpose. A Script Task can reach
/// any connection manager in the package, and which ones it touches is decided inside its script
/// text -- something this generator reads but deliberately does not parse or rewrite. Rather than
/// invent a narrower shape that guesses which configured values a port will want, the port is
/// handed the app's own service provider and resolves what it needs (e.g.
/// <c>IOptions&lt;FileSourceOptions&gt;</c> for a CSV path). That is defensible because a fill is
/// hand-written and human-reviewed; it would not be defensible for generated code.
/// </summary>
/// <param name="Uow">The package's single shared connection and transaction. A Script Task's own
/// SQL therefore participates in the same all-or-nothing transaction as every data flow -- a
/// deliberate difference from SSIS, which auto-commits per task.</param>
/// <param name="Load">Run identity: RunId, StartedAtUtc, PackageName.</param>
/// <param name="Variables">Shared package variables -- the only channel between two ported Script
/// Tasks. See <see cref="PackageVariables"/> for why it exists at all.</param>
/// <param name="Services">The running app's service provider, for configured values a port needs
/// that nothing else here exposes.</param>
public sealed record ScriptTaskContext(
    IUnitOfWork Uow,
    LoadContext Load,
    PackageVariables Variables,
    IServiceProvider Services);
