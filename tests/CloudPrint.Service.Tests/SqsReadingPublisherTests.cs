using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Publishing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudPrint.Service.Tests;

public class SqsReadingPublisherTests
{
    [Fact]
    public async Task Sends_to_queue_with_serialized_body()
    {
        var sqs = new Mock<IAmazonSQS>();
        SendMessageRequest? captured = null;
        sqs.Setup(c => c.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new SendMessageResponse());

        var publisher = new SqsReadingPublisher(sqs.Object, "https://sqs/readings",
            NullLogger<SqsReadingPublisher>.Instance);

        await publisher.PublishAsync(
            new DeviceReading { DeviceId = "d1", Value = 2.5m, Unit = "kg", Stable = true },
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("https://sqs/readings", captured!.QueueUrl);

        var roundTrip = JsonSerializer.Deserialize<DeviceReading>(captured.MessageBody);
        Assert.NotNull(roundTrip);
        Assert.Equal(2.5m, roundTrip!.Value);
        Assert.Equal("kg", roundTrip.Unit);
        Assert.True(roundTrip.Stable);
    }

    [Fact]
    public async Task Throws_when_queue_url_missing()
    {
        var sqs = new Mock<IAmazonSQS>();
        var publisher = new SqsReadingPublisher(sqs.Object, "", NullLogger<SqsReadingPublisher>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(new DeviceReading(), CancellationToken.None));

        sqs.Verify(c => c.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
