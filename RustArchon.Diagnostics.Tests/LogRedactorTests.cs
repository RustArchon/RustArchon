// Copyright ©2026 Scott Blomfield

namespace RustArchon.Diagnostics.Tests;

public class LogRedactorTests
{
    private const string Secret43 = "k3Jx9QpLm2VbN7tYcW1RzHdAeGuIoS5fT8qXwZaBnMc";

    // Each row: a message shaped like something the real code logs (Debug and Information level) and the secret that must not survive.
    public static TheoryData<string, string> SecretCases => new()
    {
        { "Request sent with header Authorization: Bearer abc.DEF-123_xyz", "abc.DEF-123_xyz" },
        { "Calling api with bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl" },
        { "Connecting with password=hunter2 to host", "hunter2" },
        { "Connecting with Password: hunter2 to host", "hunter2" },
        { "Settings {\"rconPassword\":\"hunter2\",\"port\":28016}", "hunter2" },
        { "Settings {\"rconPassword\":\"hunter 2 with spaces\",\"port\":28016}", "with spaces" },
        { "Server=db;Port=5432;Database=x;User Id=u;Password=s3cr3t;Pooling=true", "s3cr3t" },
        { "Host=db;Username=u;Password=s3cr3t", "s3cr3t" },
        { "X-Internal-Api-Key: 9f8e7d6c5b4a39281706f5e4d3c2b1a0", "9f8e7d6c5b4a39281706f5e4d3c2b1a0" },
        { "apikey=AbCdEf123456 used", "AbCdEf123456" },
        { "api_key: AbCdEf123456 used", "AbCdEf123456" },
        { "client_secret=zzz999 posted", "zzz999" },
        { "access_token=tok123456 refreshed", "tok123456" },
        { "GET https://example.test/hook?token=tok123456&x=1", "tok123456" },
        { "Fetching https://user:p4ss@example.test/file.zip", "p4ss" },
        { "POST /ingest/reports/3f2504e0-4f89-11d3-9a0c-0305e82c3301/" + Secret43, Secret43 },
        { "GET /ingest/plugin/3f2504e0-4f89-11d3-9a0c-0305e82c3301/" + Secret43, Secret43 },
        { "Sent archon.update 1.2.3 https://panel.test/ingest/plugin singleUseTok3n", "singleUseTok3n" },
        { "Sent archon.updater.update 1.2.3 https://panel.test/ingest/plugin singleUseTok3n", "singleUseTok3n" },
        { "Sent archon.map.upload https://panel.test/ingest/plugin-map singleUseTok3n", "singleUseTok3n" },
        { "WebSocket connecting to ws://10.0.0.5:28016/rc0nP4ssw0rd", "rc0nP4ssw0rd" },
        { "wss://game.example.test:28016/rc0nP4ssw0rd closed", "rc0nP4ssw0rd" },
        { "Loaded key -----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\nabc\n-----END RSA PRIVATE KEY-----", "MIIEowIBAAKCAQEA" },
        { "Token minted " + Secret43 + " for update", Secret43 },
    };

    [Theory]
    [MemberData(nameof(SecretCases))]
    public void Redact_removes_the_secret(string input, string secret)
    {
        var result = LogRedactor.Redact(input);

        Assert.DoesNotContain(secret, result);
        Assert.Contains(LogRedactor.Mask, result);
    }

    [Theory]
    [InlineData("Started the automatic update of Foo on server 3f2504e0-4f89-11d3-9a0c-0305e82c3301.")]
    [InlineData("Update of Foo refused: held_for_wipe until 2026-11-05T19:00:00Z")]
    [InlineData("File sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 matched")]
    [InlineData("Category RustArchon.Api.Services.ThirdPartyPluginUpdateServiceImplementationDetail logged")]
    [InlineData("Player connected: 76561198000000000 from 10.0.0.1")]
    [InlineData("Keyboard layout changed")]
    [InlineData("")]
    public void Redact_leaves_ordinary_text_alone(string input)
    {
        Assert.Equal(input, LogRedactor.Redact(input));
    }

    [Theory]
    [InlineData('y')]
    [InlineData('-')]
    [InlineData('.')]
    public void Redact_of_a_very_long_unbroken_run_finishes_in_time_instead_of_failing(char filler)
    {
        var input = new string(filler, 60_000);

        var result = LogRedactor.Redact(input);

        Assert.NotEqual(LogRedactor.Failed, result);
    }

    [Fact]
    public void Redact_of_null_is_empty()
    {
        Assert.Equal(string.Empty, LogRedactor.Redact(null));
    }

    [Fact]
    public void Redact_keeps_the_key_name_so_the_line_stays_readable()
    {
        Assert.Equal("Connecting with password=" + LogRedactor.Mask + " to host", LogRedactor.Redact("Connecting with password=hunter2 to host"));
    }

    [Fact]
    public void Redact_is_idempotent()
    {
        var once = LogRedactor.Redact("Password=s3cr3t; token=abc123 Bearer xyz.abc");

        Assert.Equal(once, LogRedactor.Redact(once));
    }

    [Fact]
    public void Redact_handles_every_secret_in_a_multi_line_exception_text()
    {
        var result = LogRedactor.Redact("System.Exception: boom\n   at X.Y() password=one\n   at Z() token=two");

        Assert.DoesNotContain("=one", result);
        Assert.DoesNotContain("=two", result);
        Assert.Contains("System.Exception: boom", result);
    }
}
