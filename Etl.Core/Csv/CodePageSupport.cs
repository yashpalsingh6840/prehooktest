using System.Text;

namespace Etl.Core.Csv;

/// <summary>
/// Registers CodePagesEncodingProvider exactly once. Encoding.GetEncoding(1252) throws
/// NotSupportedException on .NET without this -- it is not built in, unlike .NET Framework.
/// </summary>
internal static class CodePageSupport
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    public static void EnsureRegistered() => _ = Registered.Value;
}
