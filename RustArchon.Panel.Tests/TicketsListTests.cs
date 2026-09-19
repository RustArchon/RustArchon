// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
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

/// <summary>Tests for <see cref="TicketsList"/> - the tenant-facing "My Tickets" list.</summary>
public class TicketsListTests : BunitContext
{
    private readonly Mock<ITicketApiClient> _ticketClient = new();

    public TicketsListTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        Services.AddSingleton(_ticketClient.Object);
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-user");
    }

    [Fact]
    public void RendersEachTicketWithItsQueueAndStatus()
    {
        var ticketId = Guid.NewGuid();
        _ticketClient.Setup(c => c.ListAsync()).ReturnsAsync(
        [
            new TicketSummaryDto
            {
                Id = ticketId, Subject = "Can't connect", QueueName = "Support",
                Status = new TicketStatusDto { Name = "Open", Slug = "open" }, SubmittedOn = DateTimeOffset.UtcNow
            }
        ]);

        var cut = Render<TicketsList>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Can't connect", cut.Markup);
            Assert.Contains("Support", cut.Markup);
            Assert.Contains($"Tickets/{ticketId}", cut.Markup);
        });
    }

    [Fact]
    public void NoTickets_ShowsTheEmptyState()
    {
        _ticketClient.Setup(c => c.ListAsync()).ReturnsAsync([]);

        var cut = Render<TicketsList>();

        cut.WaitForAssertion(() => Assert.Contains("No tickets yet.", cut.Markup));
    }
}
