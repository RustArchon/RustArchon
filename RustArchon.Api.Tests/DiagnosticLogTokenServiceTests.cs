// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// The diagnostic log token: one at a time, shown once, stored only as a hash, and refused identically whether it is wrong, malformed, replaced or
/// revoked. Runs against an in-memory stand-in for the one platform-setting row it lives in.
/// </summary>
public class DiagnosticLogTokenServiceTests
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (DiagnosticLogTokenService Service, PlatformSetting Row, MutableTime Time) Create()
    {
        var row = new PlatformSetting { Key = PlatformSettingsRegistry.DiagnosticLogToken, ValueType = PlatformSettingValueType.Secret, Value = string.Empty };
        var repository = new Mock<IPlatformSettingRepository>();
        repository.Setup(r => r.GetByKeyAsync(PlatformSettingsRegistry.DiagnosticLogToken)).ReturnsAsync(() => row);
        repository.Setup(r => r.UpdateAsync(It.IsAny<PlatformSetting>())).ReturnsAsync((PlatformSetting s) => s);
        var time = new MutableTime();
        var service = new DiagnosticLogTokenService(
            repository.Object, new ApiKeyProtector(Provider), time, NullLogger<DiagnosticLogTokenService>.Instance);
        return (service, row, time);
    }

    [Fact]
    public async Task WithNoTokenEverCreatedTheFeatureIsOffWhateverIsPresented()
    {
        var (service, _, _) = Create();

        Assert.Equal(DiagnosticLogTokenState.Off, (await service.CheckAsync("anything")).State);
        Assert.Equal(DiagnosticLogTokenState.Off, (await service.CheckAsync(null)).State);
        Assert.Null(await service.GetStatusAsync());
    }

    [Fact]
    public async Task CreatingReturnsATokenOnceAndItThenChecksValid()
    {
        var (service, _, time) = Create();

        var created = await service.CreateAsync("admin@example.com");
        var check = await service.CheckAsync(created.Token);

        Assert.Equal(DiagnosticLogTokenState.Valid, check.State);
        Assert.Equal(created.TokenId, check.TokenId);
        Assert.Equal(time.Now, created.CreatedAtUtc);
        Assert.Matches("^[0-9a-f]{8}\\.[A-Za-z0-9_-]{43}$", created.Token);
        Assert.StartsWith(created.TokenId + ".", created.Token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusShowsTheIdWhoMadeItAndWhenButNeverTheSecret()
    {
        var (service, row, _) = Create();
        var created = await service.CreateAsync("admin@example.com");

        var status = await service.GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal(created.TokenId, status.TokenId);
        Assert.Equal("admin@example.com", status.CreatedBy);
        Assert.Null(status.RevokedAtUtc);
        var secret = created.Token[(created.TokenId.Length + 1)..];
        Assert.DoesNotContain(secret, row.Value);
        var stored = new ApiKeyProtector(Provider).Unprotect(ApiKeyProtectorPurposes.DiagnosticLogToken, row.Value);
        Assert.DoesNotContain(secret, stored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodot")]
    [InlineData(".")]
    [InlineData("12345678.")]
    [InlineData(".secret")]
    [InlineData("a.b.c")]
    public async Task MalformedTokensAreInvalid(string presented)
    {
        var (service, _, _) = Create();
        await service.CreateAsync("admin");

        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync(presented)).State);
    }

    [Fact]
    public async Task ANullOrOverlongTokenIsInvalid()
    {
        var (service, _, _) = Create();
        await service.CreateAsync("admin");

        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync(null)).State);
        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync(new string('a', 10_000))).State);
    }

    [Fact]
    public async Task AWrongSecretOrWrongIdIsInvalid()
    {
        var (service, _, _) = Create();
        var created = await service.CreateAsync("admin");
        var id = created.TokenId;

        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync(id + "." + new string('A', 43))).State);
        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync("00000000" + created.Token[8..])).State);
    }

    [Fact]
    public async Task RotatingReplacesTheTokenAndTheOldOneStopsWorking()
    {
        var (service, _, _) = Create();
        var first = await service.CreateAsync("admin");

        var second = await service.CreateAsync("admin");

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync(first.Token)).State);
        Assert.Equal(DiagnosticLogTokenState.Valid, (await service.CheckAsync(second.Token)).State);
    }

    [Fact]
    public async Task RevokingMakesTheTokenInvalidNotOffSoItIsRefusedLikeAnyOtherWrongToken()
    {
        var (service, _, time) = Create();
        var created = await service.CreateAsync("admin");
        time.Now = time.Now.AddMinutes(5);

        var revoked = await service.RevokeAsync("admin");

        Assert.True(revoked);
        Assert.Equal(DiagnosticLogTokenState.Invalid, (await service.CheckAsync(created.Token)).State);
        var status = await service.GetStatusAsync();
        Assert.Equal(time.Now, status!.RevokedAtUtc);
    }

    [Fact]
    public async Task RevokingWhenThereIsNothingToRevokeSaysSo()
    {
        var (service, _, _) = Create();

        Assert.False(await service.RevokeAsync("admin"));
    }

    [Fact]
    public async Task AfterRevokingANewTokenCanBeCreatedAndWorks()
    {
        var (service, _, _) = Create();
        await service.CreateAsync("admin");
        await service.RevokeAsync("admin");

        var created = await service.CreateAsync("admin");

        Assert.Equal(DiagnosticLogTokenState.Valid, (await service.CheckAsync(created.Token)).State);
        Assert.Null((await service.GetStatusAsync())!.RevokedAtUtc);
    }

    [Fact]
    public async Task ACorruptStoredValueFailsClosedAsInvalid()
    {
        var (service, row, _) = Create();
        await service.CreateAsync("admin");
        row.Value = "garbage-that-cannot-be-unprotected";

        var check = await service.CheckAsync("12345678." + new string('A', 43));

        Assert.Equal(DiagnosticLogTokenState.Invalid, check.State);
    }
}
