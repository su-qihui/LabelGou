using LabelGou.App.Printing;
using LabelGou.App.ViewModels;
using LabelGou.Core.Printing;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 84 棒补正：还的是<strong>驱动自己的默认</strong>，不是"我们进来前那份快照"。
/// <para>用户实测"没成功"，日志与注册表都对得上：退出时确实"已恢复 3848 字节"，可那份快照本身就是
/// 早期版本写进去的自定义 280×200 + 私有纸盘号——<strong>快照只能回到我们手够得着的最早那一刻</strong>，
/// 那之前已经脏了，恢复就是把脏值再放回去一遍。所以目标换成 <c>DM_OUT_DEFAULT</c>
/// （驱动页那颗「恢复默认设置」回到的那一套），并按他要求在 ⑤ 步加一颗开关。</para>
/// </summary>
public class PrinterDefaultsRestoreTests
{
    private static void OnSta(Action work)
        => LabelGou.App.Export.StaWorker
            .RunAsync<bool>((_, _) => { work(); return true; }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    [Fact]
    public void TheRestoreTargetIsTheDriversOwnDefaultNotOurSnapshot()
    {
        var driver = new byte[] { 1, 1, 1 };
        var snapshot = new byte[] { 9, 9, 9 };        // 可能就是早期版本写脏的那份

        var (target, how, fellBack) = PrinterDefaultsGuard.ChooseRestoreTarget(driver, snapshot);

        Assert.Equal(driver, target);
        Assert.Equal("驱动默认", how);
        Assert.False(fellBack);
    }

    /// <summary>问不到驱动默认时才退回：有快照用快照，原本没值就删掉那条值——两条都要标"退了一步"，日志得说清。</summary>
    [Fact]
    public void WhenTheDriverCannotBeAskedWeFallBackOneStepAtATime()
    {
        var snapshot = new byte[] { 9, 9, 9 };

        var (bySnapshot, howSnapshot, fell1) = PrinterDefaultsGuard.ChooseRestoreTarget(null, snapshot);
        Assert.Equal(snapshot, bySnapshot);
        Assert.Equal("退回快照", howSnapshot);
        Assert.True(fell1);

        var (byDelete, howDelete, fell2) = PrinterDefaultsGuard.ChooseRestoreTarget(null, null);
        Assert.Null(byDelete);
        Assert.Equal("删掉这条值", howDelete);
        Assert.True(fell2);
    }

    /// <summary>
    /// 真机一条：驱动默认要么拿回来一份能解码的 DEVMODE，要么说人话——不许抛、也不许默默给一份当前设置糊过去
    /// （那等于什么都没还）。这台电脑上真有打印机可测（§六 环境事实），所以这条不跳过。
    /// </summary>
    [Fact]
    public void TheDriversOwnDefaultComesBackDecodableOrSaysWhyNot()
    {
        var current = PrinterSettingsReader.Read(null);
        Assert.True(current.Error is null, $"读默认打印机失败：{current.Error}");

        var blob = PrinterSettingsReader.MachineDefaultDevMode(current.PrinterName, out var error);
        Assert.True(blob is not null || !string.IsNullOrWhiteSpace(error),
            "既没拿到驱动默认，也没说清为什么——那就是静默失败");
        if (blob is not null)
        {
            Assert.True(blob.Length >= 220, $"这份驱动默认只有 {blob.Length} 字节，不像一份 DEVMODE");
            var facts = DevModeFacts.Read(blob);
            Assert.True(facts.WidthMm >= 0 && facts.LengthMm >= 0);   // 读得出公开字段，没读出 NaN
        }
    }

    [Fact]
    public void ABogusPrinterNameSaysWhyInsteadOfThrowing()
    {
        var blob = PrinterSettingsReader.MachineDefaultDevMode("这台打印机根本不存在-B84", out var error);

        Assert.Null(blob);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("打印机", error);
    }

    /// <summary>那颗勾的接线：默认开，关掉就同步到退出那条路（App 拿不到 VM，只能读这个静态开关）。</summary>
    [Fact]
    public void TheSwitchDefaultsOnAndGatesTheExitRestore() => OnSta(() =>
    {
        var export = new MainViewModel(TestEnvironment.NewTempUiStateStore()).Export;

        Assert.True(export.RestorePrinterDefaults);
        Assert.True(PrinterDefaultsGuard.RestoreAtExit);

        export.RestorePrinterDefaults = false;
        Assert.False(PrinterDefaultsGuard.RestoreAtExit);

        export.RestorePrinterDefaults = true;      // 别把开关留在关着的状态影响别的判据
        Assert.True(PrinterDefaultsGuard.RestoreAtExit);
    });
}
