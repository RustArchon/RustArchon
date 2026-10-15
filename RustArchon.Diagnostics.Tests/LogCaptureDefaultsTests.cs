// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class LogCaptureDefaultsTests
{
    [Theory]
    [InlineData("RustArchon.Api.Services.ThirdPartyPluginUpdateService", LogLevel.Debug, false)]
    [InlineData("RustArchon.Panel.Services.LogReadProxy", LogLevel.Information, false)]
    [InlineData("JumpStart.Repositories.Foo", LogLevel.Debug, false)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information, true)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Debug, true)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning, false)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Debug, true)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information, true)]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Error, false)]
    [InlineData("MassTransit.Messages", LogLevel.Debug, true)]
    [InlineData("MassTransit", LogLevel.Information, true)]
    [InlineData("MassTransit", LogLevel.Warning, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Information, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Debug, true)]
    [InlineData("Npgsql.Command", LogLevel.Debug, true)]
    [InlineData("SomeLibrary.Thing", LogLevel.Critical, false)]
    public void Framework_chatter_below_warning_is_left_out_and_our_own_code_never_is(string category, LogLevel level, bool noise)
    {
        Assert.Equal(noise, LogCaptureDefaults.IsFrameworkNoise(category, level));
    }
}
