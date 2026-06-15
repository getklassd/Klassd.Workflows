// Polyfill so `init` accessors / positional records compile on netstandard2.0 (the analyzer TFM).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
