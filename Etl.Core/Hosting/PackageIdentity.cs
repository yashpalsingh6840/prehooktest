namespace Etl.Core.Hosting;

/// <summary>
/// The package's own name, set once at <see cref="EtlHost.Create"/> and resolved from DI
/// wherever it's needed (building the IEtlPackage, notification subjects, log scopes) instead of
/// being repeated as a separate string literal at each call site.
/// </summary>
public sealed record PackageIdentity(string Name);
