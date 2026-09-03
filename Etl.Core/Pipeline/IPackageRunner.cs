using Etl.Core.Abstractions;

namespace Etl.Core.Pipeline;

/// <summary>Seam over <see cref="PackageRunner"/> so a Program.cs (and its email notifier call)
/// depends on an abstraction, and so a package's own end-to-end wiring is fakeable in tests.</summary>
public interface IPackageRunner
{
    Task<PackageResult> RunAsync(IEtlPackage package, CancellationToken ct);
}
