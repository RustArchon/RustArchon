// Copyright ©2026 Scott Blomfield

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Refit;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// What a report looks like between the Api and the Panel. The pane's own tests hand it finished DTOs, so they cannot see a value the
/// Panel is unable to read off the wire - which is how a report merged from both delivery routes (<c>"Native, Plugin"</c>) broke the
/// whole inbox until it was opened in a browser. This writes the way the Api does and reads the way the Panel's Refit client does.
/// </summary>
public class ServerReportWireFormatTests
{
    // The Api registers JsonStringEnumConverter (Program.cs AddJsonOptions); the Panel uses Refit's defaults.
    private static readonly JsonSerializerOptions ApiOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions PanelOptions = SystemTextJsonContentSerializer.GetDefaultJsonSerializerOptions();

    private static ServerReportListDto ThroughTheWire(ServerReportListDto sent) =>
        JsonSerializer.Deserialize<ServerReportListDto>(JsonSerializer.Serialize(sent, ApiOptions), PanelOptions)!;

    [Theory]
    [InlineData(ServerReportSource.None)]
    [InlineData(ServerReportSource.Native)]
    [InlineData(ServerReportSource.Plugin)]
    [InlineData(ServerReportSource.Native | ServerReportSource.Plugin)]
    public void EverySourceTheApiCanSendIsReadByThePanel(ServerReportSource source)
    {
        var assignee = Guid.NewGuid();
        var sent = new ServerReportListDto
        {
            Items = [new ServerReportDto
            {
                Id = Guid.NewGuid(), Source = source, Type = ServerReportType.Cheat, Status = ServerReportStatus.Reviewing,
                Subject = "Cheating", AssignedToUserId = assignee
            }],
            TotalCount = 1, PageNumber = 1, PageSize = 25
        };

        var received = ThroughTheWire(sent).Items[0];

        Assert.Equal(source, received.Source);
        Assert.Equal(ServerReportType.Cheat, received.Type);
        Assert.Equal(ServerReportStatus.Reviewing, received.Status);
        Assert.Equal(assignee, received.AssignedToUserId);
    }

    [Fact]
    public void AReportFromBothRoutesReallyIsSentAsACommaSeparatedName()
    {
        // The reason the property carries its own converter: pin the shape so nobody "simplifies" it away and reopens the bug.
        var json = JsonSerializer.Serialize(
            new ServerReportDto { Source = ServerReportSource.Native | ServerReportSource.Plugin }, ApiOptions);

        Assert.Contains("\"Native, Plugin\"", json);
    }

    [Fact]
    public void AnUnassignedReportComesBackUnassigned()
    {
        var sent = new ServerReportListDto { Items = [new ServerReportDto { Id = Guid.NewGuid(), Source = ServerReportSource.Native }] };

        Assert.Null(ThroughTheWire(sent).Items[0].AssignedToUserId);
    }
}
