using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudPrint.Service.Tests;

public class SqsJobSourceTests
{
    private static ResolvedLane Lane(string printer = "Zebra", string queueUrl = "https://sqs/q1") =>
        new(PrinterName: printer, QueueUrl: queueUrl, PdfRenderDpi: 300, PdfFitMode: "Margins");

    private static SqsJobSource CreateSource(IAmazonSQS client, ResolvedLane? lane = null) =>
        new(client, lane ?? Lane(), visibilityTimeoutSeconds: 300, NullLogger<SqsJobSource>.Instance);

    [Fact]
    public async Task Receive_uses_lane_queue_url_and_returns_envelope()
    {
        var sqs = new Mock<IAmazonSQS>();
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            Copies = 2
        };

        sqs.Setup(c => c.ReceiveMessageAsync(
                It.Is<ReceiveMessageRequest>(r =>
                    r.QueueUrl == "https://sqs/lane-a" &&
                    r.MaxNumberOfMessages == 1 &&
                    r.WaitTimeSeconds == 20 &&
                    r.VisibilityTimeout == 300),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse
            {
                Messages = new List<Message>
                {
                    new()
                    {
                        MessageId = "msg-1",
                        Body = JsonSerializer.Serialize(job),
                        ReceiptHandle = "receipt-abc"
                    }
                }
            });

        var source = CreateSource(sqs.Object, Lane("Zebra", "https://sqs/lane-a"));

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(envelope);
        Assert.Equal("msg-1", envelope.Id);
        Assert.Equal("receipt-abc", envelope.ReceiptHandle);
        Assert.Equal("application/vnd.zebra.zpl", envelope.Job.ContentType);
        Assert.Equal(2, envelope.Job.Copies);
    }

    [Fact]
    public async Task Receive_returns_null_when_no_messages()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(c => c.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = new List<Message>() });

        var source = CreateSource(sqs.Object);

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.Null(envelope);
    }

    [Fact]
    public async Task Receive_returns_null_when_response_has_null_messages_list()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(c => c.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = null });

        var source = CreateSource(sqs.Object);

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.Null(envelope);
    }

    [Fact]
    public async Task Receive_returns_null_when_message_body_is_invalid_json()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(c => c.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse
            {
                Messages = new List<Message>
                {
                    new() { MessageId = "msg-bad", Body = "null", ReceiptHandle = "r" }
                }
            });

        var source = CreateSource(sqs.Object);

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.Null(envelope);
    }

    [Fact]
    public async Task Acknowledge_success_deletes_message_from_lane_queue()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(c => c.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        var source = CreateSource(sqs.Object, Lane("Zebra", "https://sqs/lane-b"));

        var envelope = new JobEnvelope
        {
            Id = "msg-2",
            Job = new PrintJobMessage(),
            ReceiptHandle = "receipt-xyz"
        };

        await source.AcknowledgeAsync(envelope, success: true, error: null, CancellationToken.None);

        sqs.Verify(c => c.DeleteMessageAsync(
            "https://sqs/lane-b", "receipt-xyz", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Acknowledge_failure_does_not_delete_message()
    {
        var sqs = new Mock<IAmazonSQS>();
        var source = CreateSource(sqs.Object);

        var envelope = new JobEnvelope
        {
            Id = "msg-3",
            Job = new PrintJobMessage(),
            ReceiptHandle = "receipt-fail"
        };

        await source.AcknowledgeAsync(envelope, success: false, error: "boom", CancellationToken.None);

        sqs.Verify(c => c.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Acknowledge_success_without_receipt_handle_does_not_throw()
    {
        var sqs = new Mock<IAmazonSQS>();
        var source = CreateSource(sqs.Object);

        var envelope = new JobEnvelope
        {
            Id = "msg-4",
            Job = new PrintJobMessage(),
            ReceiptHandle = null
        };

        await source.AcknowledgeAsync(envelope, success: true, error: null, CancellationToken.None);

        sqs.Verify(c => c.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Two_lanes_with_distinct_queue_urls_isolate_their_polling()
    {
        // Each lane should poll its own queue and only its own queue.
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(c => c.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = new List<Message>() });

        var laneA = CreateSource(sqs.Object, Lane("Zebra", "https://sqs/lane-A"));
        var laneB = CreateSource(sqs.Object, Lane("HP", "https://sqs/lane-B"));

        await laneA.ReceiveAsync(CancellationToken.None);
        await laneB.ReceiveAsync(CancellationToken.None);

        sqs.Verify(c => c.ReceiveMessageAsync(
            It.Is<ReceiveMessageRequest>(r => r.QueueUrl == "https://sqs/lane-A"),
            It.IsAny<CancellationToken>()), Times.Once);
        sqs.Verify(c => c.ReceiveMessageAsync(
            It.Is<ReceiveMessageRequest>(r => r.QueueUrl == "https://sqs/lane-B"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Receive_propagates_visibility_timeout_from_constructor()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(c => c.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = new List<Message>() });

        var source = new SqsJobSource(sqs.Object, Lane(), visibilityTimeoutSeconds: 600, NullLogger<SqsJobSource>.Instance);

        await source.ReceiveAsync(CancellationToken.None);

        sqs.Verify(c => c.ReceiveMessageAsync(
            It.Is<ReceiveMessageRequest>(r => r.VisibilityTimeout == 600),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
