// Win7 变体：目标框架 net6.0 缺 C# 11 `required` 成员所需的编译器标记属性（.NET 7+ 才内置）。
// 这里补齐——编译器按全名识别，internal 即可、不进公开 API。
// 用 #if 守住：若哪天把 TFM 升回 net7/net8，这段自动排除，不与 BCL 内置的同名属性撞车。
// 刻意 internal：App.Tests 同时引用 Core 与 App，若两边都 public 会造成 CS0433 类型二义性。
#if !NET7_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);

        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
#endif
