using System.Globalization;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Android.Native;

public class DobbyHook : IHook
{
    public bool SupportsRuntimeUnhook => false;

    public nint Hook(nint target, nint detour)
    {
        if (Dobby.Hook(target, detour, out var origin, nameof(DobbyHook)) != 0)
            return nint.Zero;
        return origin;
    }

    public bool Unhook(nint target)
    {
        return Dobby.Destroy(target) == 0;
    }

    public nint GetFunction(string library, string name)
    {
        return Dobby.SymbolResolver(library, name);
    }

    public nint GetFunctionRVA(string library, long rva)
    {
        var soName = library.EndsWith(".so", StringComparison.Ordinal)
            ? library
            : library + ".so";

        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            if (!line.EndsWith(soName, StringComparison.Ordinal))
                continue;

            var dash = line.IndexOf('-');
            if (dash < 0)
                continue;

            if (long.TryParse(
                    line.AsSpan(0, dash),
                    NumberStyles.HexNumber,
                    provider: null,
                    out var baseAddress))
            {
                return (nint)baseAddress + (nint)rva;
            }
        }

        return nint.Zero;
    }
}
