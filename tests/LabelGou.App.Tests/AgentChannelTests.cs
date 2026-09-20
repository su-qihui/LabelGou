using System.IO;
using LabelGou.App.Services.Agent;
using LabelGou.App.Services.Recognition;
using LabelGou.Core.Agent;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 外部 Agent 那条链的判据（第 86 棒）。
/// <para>这一页<strong>不起真进程</strong>——真跑一次归用户（他那台代理开着才能比质量）。这里钉的是
/// "发出去之前就该拦住的东西"与"改一处忘一处的那几份算式"：真 argv 过不过裁判、命令名怎么解析成
/// <c>CreateProcess</c> 起得来的东西、图片扩展名从哪一处算、超时的 0 是不是两边同口径、设置读不读得到默认。</para>
/// <para>每条负向判据都配一条正向（合规样本必须先过），否则"报了违规"可能是恒真的假绿（§五-50/118 那一族）。</para>
/// </summary>
public class AgentChannelTests
{
    static string WorkDir => Path.Combine(Path.GetTempPath(), "LabelGou", "agent-test-round");

    [Fact]
    public void 真发出去的那串参数过得了沙箱裁判()
    {
        var argv = CodexCliRuntime.BuildArgv(WorkDir, WorkDir + "\\schema.json", WorkDir + "\\answer.json",
            new[] { WorkDir + "\\sheet-1.png" });

        Assert.Empty(AgentSandboxPolicy.Validate(argv, WorkDir, CodexCliRuntime.ForbiddenRoots()));
    }

    [Fact]
    public void 少带产物文件开关_裁判立刻拦()
    {
        // 上一那条不是恒绿：把答案文件那一颗摘掉，它必须红——否则"退出码 0 就算成"会悄悄回来。
        var argv = new List<string>(CodexCliRuntime.BuildArgv(WorkDir, WorkDir + "\\schema.json",
            WorkDir + "\\answer.json", Array.Empty<string>()));
        argv.Remove("-o");
        argv.Remove(WorkDir + "\\answer.json");

        Assert.Contains(AgentSandboxPolicy.Validate(argv, WorkDir, CodexCliRuntime.ForbiddenRoots()),
            v => v.Contains("-o"));
    }

    [Fact]
    public void 禁区里真的有源码仓与用户数据根()
    {
        var roots = CodexCliRuntime.ForbiddenRoots();
        Assert.Contains(roots, r => File.Exists(Path.Combine(r, "LabelGou.sln")));   // 源码仓：不给它写权限
        Assert.Contains(roots, r => r.Length > 0);
    }

    [Fact]
    public void 提示词走标准输入不当参数递()
    {
        // Windows 命令行约 32K 封顶，而一份整表画像能到十万字：当参数递会在 CreateProcess 那一步直接死。
        var argv = CodexCliRuntime.BuildArgv(WorkDir, "s", "a", Array.Empty<string>());
        Assert.Equal(ExternalAgentProcessHost.PromptFromStdIn, argv[^1]);
    }

    [Fact]
    public void 全路径指到批处理也要包一层cmd()
    {
        var batch = Path.Combine(Path.GetTempPath(), "lg-probe-run.cmd");
        File.WriteAllText(batch, "@echo off\r\n");
        try
        {
            var resolved = ExternalAgentProcessHost.ResolveCommand(batch);
            Assert.NotNull(resolved);
            Assert.Equal("cmd.exe", resolved!.Value.FileName);
            Assert.Contains(batch, resolved.Value.PrefixArgs[^1]);
        }
        finally
        {
            File.Delete(batch);
        }
    }

    [Fact]
    public void 认得出的系统命令解析成绝对路径()
    {
        var resolved = ExternalAgentProcessHost.ResolveCommand("cmd");
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved!.Value.FileName));
        Assert.EndsWith("cmd.exe", resolved.Value.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 找不到的命令算没装而不是硬起()
    {
        Assert.Null(ExternalAgentProcessHost.ResolveCommand("labelgou-no-such-command-xyz"));
        Assert.Null(ExternalAgentProcessHost.ResolveCommand(Path.Combine(Path.GetTempPath(), "nope-xyz.exe")));
    }

    [Fact]
    public void 图片扩展名与MIME只有一处算法()
    {
        // 存成 .png 的 JPEG 字节 = 外部 runtime 看到的是坏文件。来回一趟钉住这一处。
        Assert.Equal(".jpg", ImageForModel.ExtensionFor("image/jpeg"));
        Assert.Equal("image/png", ImageForModel.MimeTypeOf("sheet-3" + ImageForModel.ExtensionFor("image/png")));
        Assert.Equal("image/jpeg", ImageForModel.MimeTypeOf("x" + ImageForModel.ExtensionFor("image/pjpeg")));
        Assert.Equal(".png", ImageForModel.ExtensionFor("application/octet-stream"));   // 认不出按 png，不猜
    }

    [Fact]
    public void 超时口径两条通道同值()
    {
        // 老实说清这条钉得住什么：两边都转调同一份 TimeoutPolicy 之后，它挡的是"以后有人只改一边"，
        // 挡不住"有人把另一份手抄回来"（值相同的手抄这条测不出来——别再拿它当"只有一处算法"的证据，§五-162）。
        // 真·单处算法的证据是源码里 RecognitionSettings 只有一句转调。
        foreach (var seconds in new[] { 0, 5, 45, 600 })
            Assert.Equal(new RecognitionSettings { TimeoutSeconds = seconds }.EffectiveTimeoutSeconds,
                TimeoutPolicy.Effective(seconds));
    }

    [Fact]
    public void 默认不开外部Agent()
    {
        // 默认值就是产品（§五-116）：外部 agent 只算增强，没装、没开都得照原路走。
        Assert.False(AgentSettings.DefaultUseExternalAgent);
        Assert.False(new AgentSettings().UseExternalAgent);
        Assert.False(new AgentSettings().ServeExternalClients);
    }

    [Fact]
    public void 设置文件读坏了要说人话而不是静默换默认()
    {
        var path = Path.Combine(Path.GetTempPath(), "lg-agent-broken.json");
        File.WriteAllText(path, "{ 这不是 JSON ");
        try
        {
            var (settings, failure) = AgentSettings.LoadFrom(path);
            Assert.False(settings.UseExternalAgent);
            Assert.Contains("读不了", failure);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 设置文件从用户数据根走()
    {
        // §五-95：新的"用户可写目录"一律从 UserPaths.Root 下来，别再制造第四个自拼 %APPDATA% 的。
        Assert.Equal(Path.Combine(LabelGou.Core.UserPaths.Root, "agent.json"), AgentSettings.FilePath);
    }

    [Fact]
    public void 版本号取最后一颗_认不出不瞎猜()
        => Assert.Equal("0.144.4", AgentRuntimeProbe.FirstWord("codex-cli 0.144.4"));
}
