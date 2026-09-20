// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JumpStart.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RustArchon.Messaging.Contracts;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The servers list after adding moved to the wizard (ADR-0002): Add is a link to it, the inline card is for editing only (and now has
/// Verify buttons), and a server that is not connected has a way back into setup.
/// </summary>
public class ServersListTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Mock<IIntegrationApiClient> _integration = new();
    private readonly Guid _id = Guid.NewGuid();

    public ServersListTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(_integration.Object);
        Services.AddSingleton(ReportTestSupport.Localizer());
        _client.Setup(c => c.UpdateAsync(_id, It.IsAny<UpdateRustServerDto>())).ReturnsAsync(new RustServerDto { Id = _id });
        GivenServers(Server(RconConnectionStatus.Connected));
    }

    private RustServerDto Server(RconConnectionStatus status, bool enabled = true, string name = "Alpha", Guid? id = null) => new()
    {
        Id = id ?? _id, Name = name, Host = "192.0.2.10", Port = 28016, IsEnabled = enabled, ConnectionStatus = status
    };

    private void GivenServers(params RustServerDto[] servers) =>
        _client.Setup(c => c.GetAllAsync()).ReturnsAsync(new PagedResult<RustServerDto> { Items = servers });

    [Fact]
    public void AddServerIsALinkToTheWizardNotAButtonThatOpensAForm()
    {
        var cut = Render<ServersList>();

        var add = cut.Find("[data-testid=add-server]");
        Assert.Equal("a", add.TagName.ToLowerInvariant());
        Assert.Equal("servers/new", add.GetAttribute("href"));
    }

    [Fact]
    public void NoAddFormIsEverShownOnThisPage()
    {
        var cut = Render<ServersList>();

        Assert.Empty(cut.FindAll("form"));
        Assert.Empty(cut.FindAll(".card-header"));
    }

    [Fact]
    public void ThePlanLimitIsNoLongerCheckedHereBecauseTheWizardDoesIt()
    {
        Render<ServersList>();

        _client.Verify(c => c.GetPlanLimitAsync(), Times.Never);
    }

    [Theory]
    [InlineData(RconConnectionStatus.Error, true, true)]
    [InlineData(RconConnectionStatus.Disconnected, true, true)]
    [InlineData(RconConnectionStatus.Connecting, true, true)]
    [InlineData(RconConnectionStatus.Reconnecting, true, true)]
    [InlineData(RconConnectionStatus.Connected, true, false)]
    [InlineData(RconConnectionStatus.Error, false, false)] // disabled on purpose: nothing to finish
    public void OnlyAnEnabledServerThatIsNotConnectedOffersToFinishSetup(RconConnectionStatus status, bool enabled, bool expectLink)
    {
        GivenServers(Server(status, enabled));

        var cut = Render<ServersList>();

        var links = cut.FindAll("[data-testid=resume-setup]");
        Assert.Equal(expectLink, links.Count == 1);
        if (expectLink)
        {
            Assert.Equal($"servers/{_id}/setup", links[0].GetAttribute("href"));
        }
    }

    [Fact]
    public void TheLinkIsPerServer()
    {
        var other = Guid.NewGuid();
        GivenServers(Server(RconConnectionStatus.Connected, name: "Alpha"), Server(RconConnectionStatus.Error, name: "Beta", id: other));

        var cut = Render<ServersList>();

        var link = Assert.Single(cut.FindAll("[data-testid=resume-setup]"));
        Assert.Equal($"servers/{other}/setup", link.GetAttribute("href"));
    }

    private static void OpenEdit(IRenderedComponent<ServersList> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Edit")).Click();

    [Fact]
    public void EditingStillWorksAndSavesAnUpdateNeverACreate()
    {
        var cut = Render<ServersList>();
        OpenEdit(cut);

        Assert.Equal("Edit Server", cut.Find(".card-header").TextContent.Trim());
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => _client.Verify(c => c.UpdateAsync(_id, It.IsAny<UpdateRustServerDto>()), Times.Once));
        _client.Verify(c => c.CreateAsync(It.IsAny<CreateRustServerDto>()), Times.Never);
    }

    [Fact]
    public void TheEditFormHasAVerifyButtonForTheSteamKey()
    {
        var cut = Render<ServersList>();
        OpenEdit(cut);

        Assert.True(cut.Find("[data-testid=verify-steam]").HasAttribute("disabled")); // nothing typed yet

        cut.Find("#server-steam-api-key").Input("STEAMKEY");
        Assert.False(cut.Find("[data-testid=verify-steam]").HasAttribute("disabled"));
    }

    [Fact]
    public void TheEditFormOnlyOffersToVerifyAGeolocationKeyOnceAProviderIsChosen()
    {
        var cut = Render<ServersList>();
        OpenEdit(cut);
        Assert.Empty(cut.FindAll("[data-testid=verify-geo]"));

        cut.Find("#server-geo-provider").Change(GeolocationProviderKind.IpHubInfo.ToString());

        cut.WaitForElement("[data-testid=verify-geo]");
    }

    [Fact]
    public void VerifyingInTheEditFormAsksTheProviderAndSavesNothing()
    {
        _integration.Setup(c => c.VerifyKeyAsync(It.IsAny<VerifyIntegrationKeyRequest>()))
            .ReturnsAsync(new VerifyIntegrationKeyResultDto { Verdict = IntegrationKeyVerdict.Valid });
        var cut = Render<ServersList>();
        OpenEdit(cut);
        cut.Find("#server-steam-api-key").Input("STEAMKEY");

        cut.Find("[data-testid=verify-steam]").Click();

        Assert.Contains("accepted", cut.WaitForElement("[data-testid=verify-steam-result]").TextContent);
        _client.Verify(c => c.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdateRustServerDto>()), Times.Never);
    }

    [Fact]
    public void AnEmptyListStillPointsAtAddingOne()
    {
        GivenServers();

        var cut = Render<ServersList>();

        Assert.Contains("No servers yet", cut.Find(".alert-info").TextContent);
        cut.Find("[data-testid=add-server]");
    }
}
