using JALib.Core.Patch;

namespace StArray.ModManager.Tests.JalibPatchFixtures
{
    public static class PatchTarget
    {
        public static void Run() { }
    }

    public static class PatchSet
    {
        [JAPatch(typeof(PatchTarget), nameof(PatchTarget.Run), PatchType.Prefix, false)]
        public static void Prefix() { }

        [JAPatch(typeof(PatchTarget), nameof(PatchTarget.Run), PatchType.Postfix, false)]
        public static void Postfix() { }
    }
}

namespace StArray.ModManager.Tests.JalibPatchFixtures.Child
{
    public static class ChildPatchSet
    {
        [JAPatch(
            typeof(StArray.ModManager.Tests.JalibPatchFixtures.PatchTarget),
            nameof(StArray.ModManager.Tests.JalibPatchFixtures.PatchTarget.Run),
            PatchType.Prefix,
            false)]
        public static void Prefix() { }
    }
}
