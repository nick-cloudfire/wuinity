// Polyfill for the compiler marker type behind C# 9 'init' accessors and records.
//
// The type is not part of the netstandard2.1 surface Unity compiles against, so any source in
// Assets using 'init' fails with CS0518 "Predefined type
// 'System.Runtime.CompilerServices.IsExternalInit' is not defined or imported" -- currently the
// Nelson-Dead-Fuel-Moisture submodule's WxsHelpers.cs, whose real build is a .NET 8 project where
// the type exists. Roslyn only needs the type to be present somewhere in the assembly being
// compiled; it emits no code and has no runtime cost. The vendored kPERILcore needed the same
// shim for the same reason (see its source/PriorityQueueShim.cs).
//
// Declared here rather than inside the submodule so the fix lives in our own tree and survives
// re-cloning or updating that submodule. Everything in Assets without an .asmdef compiles into
// Assembly-CSharp, so one copy covers the lot.

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

#endif
