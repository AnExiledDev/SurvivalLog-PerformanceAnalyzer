// The game's interop assemblies (HotUpdate, UnityEngine.CoreModule) expose public types with these names,
// which hide the framework's and make the compiler fail with CS0656. Declaring them here wins over both.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;

        public NullableAttribute(byte flag) => NullableFlags = new[] { flag };

        public NullableAttribute(byte[] flags) => NullableFlags = flags;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Struct, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;

        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}
