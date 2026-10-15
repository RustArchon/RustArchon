// Copyright ©2026 Scott Blomfield

using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

public class VerifiedLogTokenCacheTests
{
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ARememberedTokenIsRecognisedAndAnotherIsNot()
    {
        var cache = new VerifiedLogTokenCache(new MutableTime());
        cache.Remember("abcd1234.secret");

        Assert.True(cache.Contains("abcd1234.secret"));
        Assert.False(cache.Contains("abcd1234.other"));
        Assert.False(cache.Contains(string.Empty));
    }

    [Fact]
    public void ATokenIsForgottenAfterItsLifetime()
    {
        var time = new MutableTime();
        var cache = new VerifiedLogTokenCache(time);
        cache.Remember("abcd1234.secret");

        time.Now = time.Now.Add(VerifiedLogTokenCache.Lifetime).AddSeconds(1);

        Assert.False(cache.Contains("abcd1234.secret"));
    }

    [Fact]
    public void RememberingAgainExtendsTheLifetime()
    {
        var time = new MutableTime();
        var cache = new VerifiedLogTokenCache(time);
        cache.Remember("abcd1234.secret");
        time.Now = time.Now.AddMinutes(10);
        cache.Remember("abcd1234.secret");
        time.Now = time.Now.AddMinutes(10);

        Assert.True(cache.Contains("abcd1234.secret"));
    }

    [Fact]
    public void AForgottenTokenIsNoLongerRecognised()
    {
        var cache = new VerifiedLogTokenCache(new MutableTime());
        cache.Remember("abcd1234.secret");

        cache.Forget("abcd1234.secret");

        Assert.False(cache.Contains("abcd1234.secret"));
    }

    [Fact]
    public void TheNumberOfRememberedTokensIsBounded()
    {
        var cache = new VerifiedLogTokenCache(new MutableTime());

        for (var i = 0; i < VerifiedLogTokenCache.MaxEntries * 4; i++)
        {
            cache.Remember("token-" + i);
        }

        Assert.True(cache.Count <= VerifiedLogTokenCache.MaxEntries);
    }

    [Fact]
    public void TheCacheNeverHoldsThePlaintextToken()
    {
        var cache = new VerifiedLogTokenCache(new MutableTime());
        cache.Remember("abcd1234.PLAINTEXT-SECRET");

        Assert.DoesNotContain("PLAINTEXT-SECRET", cache.DebugView());
    }
}
