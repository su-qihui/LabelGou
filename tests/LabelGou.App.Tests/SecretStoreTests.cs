using System;
using System.IO;
using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 密钥存得住、又不能被明文寄出去的钉子（M7 第 11 棒）。
/// <para>起因是用户一句话：「填入后密钥就自动消失了？要让它保存」。第 9 棒把它定成「只活在这次运行」，
/// 结果就是设置窗填完、关窗、再开，界面上只剩「云端没有 API 密钥」。</para>
/// <para>这里同时钉住两头：<b>要存得住</b>（Load 回来还在）与<b>不能明文</b>
/// （磁盘上那个文件与 recognition.json 里都不许出现密钥本体）。少一头都是回到老问题。</para>
/// <para>所有文件都落临时目录，不碰真的 <c>%APPDATA%\LabelGou\</c>（§五-48 同一条纪律）。</para>
/// </summary>
public class SecretStoreTests
{
    private const string Key = "sk-test-9f2c47ab0d3f9";

    private static string TempPath(string name = "llm-key.protected")
        => Path.Combine(TestEnvironment.NewTempDir("labelgou-secret"), name);

    [Fact]
    public void 存进去取得回而且磁盘上没有明文()
    {
        var path = TempPath();

        Assert.Equal(SecretStore.Status.Ok, SecretStore.TryWriteFile(path, Key, out var writeFailure));
        Assert.Null(writeFailure);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(Key, raw, StringComparison.Ordinal);          // 这条是整棒的红线：存的是密文
        Assert.NotEqual(Key, raw.Trim());

        Assert.Equal(SecretStore.Status.Ok, SecretStore.TryReadFile(path, out var plain, out var readFailure));
        Assert.Null(readFailure);
        Assert.Equal(Key, plain);
    }

    [Fact]
    public void 密文被改坏时报解不开而不是当没存过()
    {
        var path = TempPath();
        SecretStore.TryWriteFile(path, Key, out _);
        var raw = File.ReadAllText(path);
        File.WriteAllText(path, raw[..(raw.Length - 8)] + "AAAAAAAA");     // 长度不变、内容被动过

        Assert.Equal(SecretStore.Status.Unreadable, SecretStore.TryReadFile(path, out var plain, out var failure));
        Assert.Null(plain);
        Assert.False(string.IsNullOrWhiteSpace(failure));                   // 界面要能拿到一句话去说，而不是静默
    }

    [Fact]
    public void 文件不存在与不是密文两件事分得开()
    {
        var path = TempPath();
        Assert.Equal(SecretStore.Status.Missing, SecretStore.TryReadFile(path, out _, out var missingFailure));
        Assert.Null(missingFailure);                                        // 没存过不算错，别拿红字吓人

        File.WriteAllText(path, "这根本不是 base64 !!");
        Assert.Equal(SecretStore.Status.Unreadable, SecretStore.TryReadFile(path, out _, out var badFailure));
        Assert.False(string.IsNullOrWhiteSpace(badFailure));
    }

    [Fact]
    public void 删干净之后读不到任何东西()
    {
        var path = TempPath();
        SecretStore.TryWriteFile(path, Key, out _);
        Assert.Null(SecretStore.TryClearFile(path));
        Assert.False(File.Exists(path));
        Assert.Equal(SecretStore.Status.Missing, SecretStore.TryReadFile(path, out _, out _));
        Assert.Null(SecretStore.TryClearFile(path));                        // 不在也算删成功，幂等
    }

    [Fact]
    public void 设置里勾了保存才落盘而且明文不进recognitionjson()
    {
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var keyPath = Path.Combine(dir, "llm-key.protected");

        var settings = new RecognitionSettings
        {
            Provider = RecognitionSettings.Providers.OpenAi,
            ApiKey = Key,
            RememberApiKey = true,
        };
        settings.SaveTo(settingsPath, keyPath);

        // 存的是这一家自己的密文文件（一家一份），不再是那个全局的 llm-key.protected
        Assert.True(File.Exists(SecretStore.KeyPathFor(keyPath, settings.ApiKeySlot)));
        Assert.DoesNotContain(Key, File.ReadAllText(settingsPath), StringComparison.Ordinal);
        Assert.Contains("\"rememberApiKey\":true", File.ReadAllText(settingsPath).Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Equal(Key, RecognitionSettings.LoadFrom(settingsPath, keyPath).ApiKey);   // 跨进程回来还在
    }

    [Fact]
    public void 关掉保存会把磁盘上那份一起清掉()
    {
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var keyPath = Path.Combine(dir, "llm-key.protected");

        var seed = new RecognitionSettings { ApiKey = Key, RememberApiKey = true };
        seed.SaveTo(settingsPath, keyPath);
        var slotKeyPath = SecretStore.KeyPathFor(keyPath, seed.ApiKeySlot);
        Assert.True(File.Exists(slotKeyPath));

        var off = RecognitionSettings.LoadFrom(settingsPath, keyPath);
        Assert.Equal(Key, off.ApiKey);                      // 先确认读回来了，否则这条测试是空的
        off.RememberApiKey = false;
        off.SaveTo(settingsPath, keyPath);

        Assert.False(File.Exists(slotKeyPath));
        Assert.Equal(SecretStore.Status.Missing, RecognitionSettings.LoadFrom(settingsPath, keyPath).SavedKeyStatus);
    }

    [Fact]
    public void 没新填密钥时不拿空值覆盖存着的那份()
    {
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var keyPath = Path.Combine(dir, "llm-key.protected");
        new RecognitionSettings { ApiKey = Key, RememberApiKey = true }.SaveTo(settingsPath, keyPath);

        var reopened = RecognitionSettings.LoadFrom(settingsPath, keyPath);
        reopened.ApiKey = null;                             // 用户在设置窗里没动密钥，只是改了别的又保存一次
        reopened.RememberApiKey = true;
        reopened.SaveTo(settingsPath, keyPath);

        Assert.Equal(Key, RecognitionSettings.LoadFrom(settingsPath, keyPath).ApiKey);
    }

    [Fact]
    public void 老设置文件没这个开关时按新默认存本地但不凭空造密钥文件()
    {
        // 第 80 棒：用户要「默认把模型 apikey 存在本地」，没写这个开关的老文件升上来就是"存"。
        // 仍然保住的那半：手上没有密钥时不许凭空造一个密钥文件出来。
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var keyPath = Path.Combine(dir, "llm-key.protected");
        File.WriteAllText(settingsPath, """{"endpoint":"http://127.0.0.1:11434","model":"qwen3-vl:4b"}""");

        var loaded = RecognitionSettings.LoadFrom(settingsPath, keyPath);

        Assert.True(loaded.RememberApiKey);
        Assert.Null(loaded.ApiKey);
        Assert.Equal(SecretStore.Status.Missing, loaded.SavedKeyStatus);
        loaded.SaveTo(settingsPath, keyPath);
        Assert.False(File.Exists(keyPath));                 // 没密钥就不该造文件
        Assert.False(File.Exists(SecretStore.KeyPathFor(keyPath, loaded.ApiKeySlot)));
    }

    [Fact]
    public void 没勾保存就不去读那个文件哪怕它在()
    {
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var keyPath = Path.Combine(dir, "llm-key.protected");
        SecretStore.TryWriteFile(keyPath, Key, out _);       // 上一轮留下的
        File.WriteAllText(settingsPath, """{"rememberApiKey":false}""");

        Assert.Null(RecognitionSettings.LoadFrom(settingsPath, keyPath).ApiKey);
    }

    [Fact]
    public void 环境变量优先于磁盘上存的那份()
    {
        var name = "LABELGOU_TEST_KEY_" + Guid.NewGuid().ToString("N")[..6];
        Environment.SetEnvironmentVariable(name, "sk-from-env-var");
        try
        {
            var settings = new RecognitionSettings { ApiKeyEnvVar = name, ApiKey = Key, RememberApiKey = true };

            Assert.Equal("sk-from-env-var", settings.ResolveApiKey());      // 第 9 棒那条优先级不许被这次改动顶掉
            Assert.Equal("环境变量", settings.ApiKeySource);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void 密钥来源说得清是谁给的()
    {
        Assert.Equal("没有密钥", new RecognitionSettings().ApiKeySource);
        Assert.Equal("本次填的", new RecognitionSettings { ApiKey = Key }.ApiKeySource);

        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var keyPath = Path.Combine(dir, "llm-key.protected");
        new RecognitionSettings { ApiKey = Key, RememberApiKey = true }.SaveTo(settingsPath, keyPath);

        Assert.Equal("本机存过", RecognitionSettings.LoadFrom(settingsPath, keyPath).ApiKeySource);
    }

    [Fact]
    public void 本次刚填的那一串能越窗口递到面板手里()
    {
        // 用户报的第二张图就是这个：设置窗里探活成功，对话窗里说“没有密钥”。
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        new RecognitionSettings().SaveTo(settingsPath);            // 磁盘上什么都没存
        var slot = RecognitionSettings.SlotOf(new RecognitionSettings().Endpoint);
        try
        {
            RecognitionSettings.RememberSessionKey(slot, Key);

            var merged = RecognitionSettings.LoadFrom(settingsPath, keyPath: null, mergeSessionKey: true);
            Assert.Equal(Key, merged.ApiKey);
            Assert.Equal("本次运行填的", merged.ApiKeySource);
            Assert.NotNull(merged.ResolveApiKey());
        }
        finally
        {
            RecognitionSettings.RememberSessionKey(slot, null);
        }
    }

    [Fact]
    public void 单测走的读文件路径不接静态会话密钥()
    {
        // 静态字段一旦被 LoadFrom 默认吃进去，一个测试设的密钥会污染下一个测试的“没有密钥”断言。
        var dir = TestEnvironment.NewTempDir("labelgou-settings");
        var settingsPath = Path.Combine(dir, "recognition.json");
        new RecognitionSettings().SaveTo(settingsPath);
        var slot = RecognitionSettings.SlotOf(new RecognitionSettings().Endpoint);
        try
        {
            RecognitionSettings.RememberSessionKey(slot, Key);

            Assert.Null(RecognitionSettings.LoadFrom(settingsPath).ApiKey);
            Assert.Null(RecognitionSettings.LoadFrom(settingsPath, keyPath: null).ApiKey);
        }
        finally
        {
            RecognitionSettings.RememberSessionKey(slot, null);
        }
    }

    [Fact]
    public void 一家一份密钥不会跟着换厂商跑过去()
    {
        // 用户 2026-09-16：「一个 apikey 是接一个厂商的（目前是在更换模型厂商时 apikey 会跟过去）」。
        // 百炼那一串塞进 DeepSeek 只会换回 401，而界面上还显示"有密钥"。
        Assert.Equal("dashscope.aliyuncs.com",
            RecognitionSettings.SlotOf("https://dashscope.aliyuncs.com/compatible-mode/v1"));
        Assert.Equal("api.deepseek.com", RecognitionSettings.SlotOf("https://api.deepseek.com"));
        Assert.Equal("本机", RecognitionSettings.SlotOf("http://127.0.0.1:11434"));
        Assert.NotEqual(RecognitionSettings.SlotOf("https://api.deepseek.com"),
            RecognitionSettings.SlotOf("https://dashscope.aliyuncs.com/compatible-mode/v1"));

        var dir = TestEnvironment.NewTempDir("labelgou-slots");
        Assert.NotEqual(SecretStore.KeyPathFor(Path.Combine(dir, "llm-key.protected"), "api.deepseek.com"),
                        SecretStore.KeyPathFor(Path.Combine(dir, "llm-key.protected"), "dashscope.aliyuncs.com"));
        Assert.Contains("dashscope", Path.GetFileName(
            SecretStore.KeyPathFor(Path.Combine(dir, "llm-key.protected"), "dashscope.aliyuncs.com")));
    }

    [Fact]
    public void 老的那一份全局密钥第一次读时迁给当前这家()
    {
        var dir = TestEnvironment.NewTempDir("labelgou-migrate");
        var settingsPath = Path.Combine(dir, "recognition.json");
        var legacyKeyPath = Path.Combine(dir, "llm-key.protected");
        new RecognitionSettings { RememberApiKey = true }.SaveTo(settingsPath);
        SecretStore.TryWriteFile(legacyKeyPath, Key, out var wrote);   // 老版现场：只有这一份全局密文
        Assert.Null(wrote);

        var back = RecognitionSettings.LoadFrom(settingsPath, legacyKeyPath);

        Assert.Equal(Key, back.ApiKey);
        Assert.True(File.Exists(SecretStore.KeyPathFor(legacyKeyPath, back.ApiKeySlot)),
            "迁移后这一家要有自己的密文文件，否则下次还得再迁一遍");
    }
}
