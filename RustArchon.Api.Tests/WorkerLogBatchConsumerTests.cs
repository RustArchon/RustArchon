// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Moq;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Api.Messaging;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

public class WorkerLogBatchConsumerTests
{
    [Fact]
    public async Task ABatchFromTheWorkerEndsUpInTheStore()
    {
        var store = new WorkerLogStore();
        var message = new WorkerLogBatch(
            "aaaaaaaa1111",
            [new WorkerLogEntry(1, DateTimeOffset.UnixEpoch, 2, "Cat", "hello", null, new Dictionary<string, string>())]);
        var context = new Mock<ConsumeContext<WorkerLogBatch>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

        await new WorkerLogBatchConsumer(store).Consume(context.Object);

        var source = Assert.Single(store.Sources());
        var result = await source.ReadAsync(null, 10, _ => true, CancellationToken.None);
        Assert.Equal("hello", Assert.Single(result.Page!.Entries).Message);
    }
}
