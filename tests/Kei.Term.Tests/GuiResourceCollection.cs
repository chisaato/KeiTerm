namespace Kei.Term.Tests;

// Headless 会话共享 Application.Resources；主题切换不能与后台投递的默认主题更新交错。
// 仅隔离直接验证全局主题资源的测试，其余测试仍可并行。
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GuiResourceCollection
{
    public const string Name = "GUI resources";
}
