using Etl.Core.Hosting;
using SyntheticScriptComponentSeams;
using SyntheticScriptComponentSeams.Model;
using Microsoft.Extensions.DependencyInjection;

const string PackageName = "SyntheticScriptComponentSeams";

var builder = EtlHost.Create(args, PackageName);
builder.Services.AddEtlDbContext<SyntheticScriptComponentSeamsDbContext>();

using var host = builder.Build();
return await new SyntheticScriptComponentSeamsPackage(host.Services).RunAsync(CancellationToken.None);
