// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Tickets;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// Tests for the tenant-facing <see cref="TicketDetail"/> - specifically that the plain
/// <c>&lt;textarea @bind="replyBody"&gt;</c> reply box (not wrapped in an <c>EditForm</c>) actually
/// carries its typed value through to <see cref="ITicketApiClient.AddMessageAsync"/> on this render
/// mode. Worth its own test: <c>Admin/Organizations.razor</c>'s own history has a real case of
/// <c>@bind</c> silently not reaching the bound field on a statically-routed, interactively-rendered
/// page - see that file's remarks.
/// </summary>
public class TicketDetailReplyTests : BunitContext
{
    private readonly Mock<ITicketApiClient> _ticketClient = new();
    private readonly Guid _ticketId = Guid.NewGuid();

    public TicketDetailReplyTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        _ticketClient.Setup(c => c.GetAsync(_ticketId)).ReturnsAsync(new TicketDetailDto
        {
            Id = _ticketId,
            Subject = "Can't connect",
            QueueName = "Support",
            Status = new TicketStatusDto { Name = "Open", Slug = "open" },
            Messages =
            [
                new TicketMessageDto
                {
                    Id = Guid.NewGuid(), AuthorType = TicketMessageAuthorType.Customer,
                    Body = "It just times out.", CreatedOn = DateTimeOffset.UtcNow
                }
            ]
        });

        Services.AddSingleton(_ticketClient.Object);
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-user");
    }

    [Fact]
    public void SendingAReply_CarriesTheTypedBodyToTheApi()
    {
        string? capturedBody = null;

        _ticketClient
            .Setup(c => c.AddMessageAsync(_ticketId, It.IsAny<SaveTicketMessageRequestDto>()))
            .Callback<Guid, SaveTicketMessageRequestDto>((_, request) => capturedBody = request.Body)
            .ReturnsAsync(new TicketMessageDto());

        var cut = Render<TicketDetail>(parameters => parameters.Add(p => p.Id, _ticketId));

        cut.WaitForAssertion(() => cut.Find("#reply-body"));

        cut.Find("#reply-body").Change("Still happening after the restart.");
        cut.FindAll("button").First(b => b.TextContent.Contains("Send Reply")).Click();

        cut.WaitForAssertion(() => Assert.Equal("Still happening after the restart.", capturedBody));
    }
}
