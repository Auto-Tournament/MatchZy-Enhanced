using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using System.Runtime.InteropServices;


namespace AutoTournamentCS2;

public static class GrenadeFunctions
{

    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>
        CSmokeGrenadeProjectile_CreateFunc = new(
            IsLinux
                ? @"55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE"
                : @"48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8"
        );

    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>
        CHEGrenadeProjectile_CreateFunc = new(
            IsLinux
                ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 49 89 D6"
                : "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 48 83 EC ? 48 8B 6C 24 ? 49 8B F8 4C 8B C2 0F 29 74 24"
        );
    
    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CMolotovProjectile>
        CMolotovProjectile_CreateFunc = new(
            IsLinux
                ? "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 81 EC ? ? ? ? 4C 8D 35"
                : "48 8B C4 48 89 58 ? 4C 89 40 ? 48 89 48 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24"
        );
    
    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CDecoyProjectile>
        CDecoyProjectile_CreateFunc = new(
            IsLinux
                ? "55 4C 89 C1 48 89 E5 41 57 45 89 CF 41 56 49 89 FE 41 55 49 89 D5 48 89 F2 48 89 FE 41 54 48 8D 3D ? ? ? ? 4D 89 C4 53 48 83 EC ? E8 ? ? ? ? 45 31 C0"
                : "48 8B C4 55 56 48 81 EC ? ? ? ? 48 89 58 ? 48 8B D9"
        );
}
