namespace HeadphoneLogger.Tests;

/// <summary>冒烟测试：确认测试基建与主程序集引用链路畅通。</summary>
public class SmokeTest
{
    [Fact]
    public void MainAssemblyLoads()
    {
        var asm = typeof(HeadphoneLogger.Program).Assembly;
        Assert.NotNull(asm);
        Assert.Equal("HeadphoneLogger", asm.GetName().Name);
    }
}
