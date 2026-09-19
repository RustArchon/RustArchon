// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Tickets;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>Tests for <see cref="NewTicket"/> - the "New Ticket" submission form.</summary>
public class NewTicketTests : BunitContext
{
    private readonly Mock<ITicketApiClient> _ticketClient = new();
    private readonly Guid _supportQueueId = Guid.NewGuid();
    private readonly Guid _bugsQueueId = Guid.NewGuid();

    public NewTicketTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        _ticketClient.Setup(c => c.GetQueuesAsync()).ReturnsAsync(
        [
            new QueueDto { Id = _bugsQueueId, Name = "Bug Reports", IsDefault = false },
            new QueueDto { Id = _supportQueueId, Name = "Support", IsDefault = true }
        ]);

        Services.AddSingleton(_ticketClient.Object);
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-user");
    }

    [Fact]
    public void TheDefaultQueue_IsPreselected()
    {
        var cut = Render<NewTicket>();

        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("#ticket-queue");
            Assert.Equal(_supportQueueId.ToString(), select.GetAttribute("value"));
        });
    }

    [Fact]
    public void Submitting_CreatesTheTicketAndNavigatesToIt()
    {
        var createdId = Guid.NewGuid();
        CreateTicketRequestDto? captured = null;

        _ticketClient
            .Setup(c => c.CreateAsync(It.IsAny<CreateTicketRequestDto>()))
            .Callback<CreateTicketRequestDto>(request => captured = request)
            .ReturnsAsync(new TicketDetailDto { Id = createdId });

        var cut = Render<NewTicket>();

        cut.WaitForAssertion(() => cut.Find("#ticket-queue"));

        cut.Find("#ticket-queue").Change(_bugsQueueId.ToString());
        cut.Find("#ticket-subject").Change("Server won't start");
        cut.Find("#ticket-body").Change("Nothing in the logs either.");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(captured);
            Assert.Equal(_bugsQueueId, captured!.QueueId);
            Assert.Equal("Server won't start", captured.Subject);
            Assert.Equal("Nothing in the logs either.", captured.Body);

            var navigation = Services.GetRequiredService<NavigationManager>();
            Assert.EndsWith($"Tickets/{createdId}", navigation.Uri);
        });
    }
}
