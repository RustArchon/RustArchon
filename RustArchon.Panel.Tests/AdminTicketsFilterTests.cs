// Copyright ©2026 Scott Blomfield

using System;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Admin;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// Tests for the staff console's <see cref="Tickets"/> list - specifically that picking a queue or
/// status in the filter bar's explicit <c>@onchange</c> handlers (not <c>@bind</c>) actually reaches
/// <see cref="IAdminTicketApiClient.ListAsync"/> with the right value. Worth its own test for the same
/// reason as <c>TicketDetailReplyTests</c>: this codebase's own <c>Admin/Organizations.razor</c> has a
/// real prior case of a filter control silently not reaching the query it was supposed to narrow.
/// </summary>
public class AdminTicketsFilterTests : BunitContext
{
    private readonly Mock<IAdminTicketApiClient> _adminTicketClient = new();
    private readonly Guid _supportQueueId = Guid.NewGuid();
    private readonly Guid _bugsQueueId = Guid.NewGuid();
    private readonly Guid _resolvedStatusId = Guid.NewGuid();

    public AdminTicketsFilterTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        _adminTicketClient.Setup(c => c.GetQueuesAsync()).ReturnsAsync(
        [
            new QueueDto { Id = _supportQueueId, Name = "Support", IsDefault = true },
            new QueueDto { Id = _bugsQueueId, Name = "Bug Reports" }
        ]);

        _adminTicketClient.Setup(c => c.GetStatusesAsync()).ReturnsAsync(
        [
            new TicketStatusDto { Id = Guid.NewGuid(), Name = "Open", Slug = "open" },
            new TicketStatusDto { Id = _resolvedStatusId, Name = "Resolved", Slug = "resolved" }
        ]);

        _adminTicketClient
            .Setup(c => c.ListAsync(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<bool?>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_adminTicketClient.Object);
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-staff");
    }

    [Fact]
    public void PickingAQueue_ReloadsWithThatQueueId()
    {
        var cut = Render<Tickets>();

        cut.WaitForAssertion(() => cut.Find("#ticket-queue-filter"));

        cut.Find("#ticket-queue-filter").Change(_bugsQueueId.ToString());

        cut.WaitForAssertion(() => _adminTicketClient.Verify(
            c => c.ListAsync(_bugsQueueId, null, false),
            Times.Once));
    }

    [Fact]
    public void PickingAStatus_ReloadsWithThatStatus()
    {
        var cut = Render<Tickets>();

        cut.WaitForAssertion(() => cut.Find("#ticket-status-filter"));

        cut.Find("#ticket-status-filter").Change(_resolvedStatusId.ToString());

        cut.WaitForAssertion(() => _adminTicketClient.Verify(
            c => c.ListAsync(null, _resolvedStatusId, false),
            Times.Once));
    }

    /// <summary>The Open/Closed/All filter keys off each status's <c>IsClosed</c> flag, not an exact
    /// status - "closed" and "all" have to reach the Api as <c>isClosed: true</c> and
    /// <c>isClosed: null</c> respectively, not just any truthy/falsy value.</summary>
    [Fact]
    public void PickingClosedInTheOpenClosedFilter_ReloadsWithIsClosedTrue()
    {
        var cut = Render<Tickets>();

        cut.WaitForAssertion(() => cut.Find("#ticket-closed-filter"));

        cut.Find("#ticket-closed-filter").Change("closed");

        cut.WaitForAssertion(() => _adminTicketClient.Verify(
            c => c.ListAsync(null, null, true),
            Times.Once));
    }

    [Fact]
    public void PickingAllInTheOpenClosedFilter_ReloadsWithIsClosedNull()
    {
        var cut = Render<Tickets>();

        cut.WaitForAssertion(() => cut.Find("#ticket-closed-filter"));

        cut.Find("#ticket-closed-filter").Change("all");

        cut.WaitForAssertion(() => _adminTicketClient.Verify(
            c => c.ListAsync(null, null, null),
            Times.Once));
    }
}
