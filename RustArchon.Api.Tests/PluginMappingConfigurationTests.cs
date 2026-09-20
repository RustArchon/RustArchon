// Copyright ©2026 Scott Blomfield

using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Mapping;

namespace RustArchon.Api.Tests;

/// <summary>
/// The Api asserts every AutoMapper configuration is valid at startup, and refuses to boot if one is not. These are
/// the same assertion for the plugin-related profiles, run in the unit tests so a DTO property added without a
/// mapping (or an Ignore) fails here instead of as a crashed Api.
/// </summary>
public class PluginMappingConfigurationTests
{
    [Fact]
    public void ServerPluginStatusMappingIsValid()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<ServerPluginStatusMappingProfile>(), NullLoggerFactory.Instance);

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void RustServerMappingIsValid()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<RustServerMappingProfile>(), NullLoggerFactory.Instance);

        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void ServerPluginMappingIsValid()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<ServerPluginMappingProfile>(), NullLoggerFactory.Instance);

        config.AssertConfigurationIsValid();
    }
}
