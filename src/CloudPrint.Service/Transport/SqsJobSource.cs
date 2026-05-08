using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;

namespace CloudPrint.Service.Transport;

public class SqsJobSource : IJobSource
{
    private readonly IAmazonSQS _sqsClient;
    private readonly ResolvedLane _lane;
    private readonly int _visibilityTimeoutSeconds;
    private readonly ILogger<SqsJobSource> _logger;

    public SqsJobSource(
        IAmazonSQS sqsClient,
        ResolvedLane lane,
        int visibilityTimeoutSeconds,
        ILogger<SqsJobSource> logger)
    {
        _sqsClient = sqsClient;
        _lane = lane;
        _visibilityTimeoutSeconds = visibilityTimeoutSeconds;
        _logger = logger;
    }

    public async Task<JobEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var request = new ReceiveMessageRequest
        {
            QueueUrl = _lane.QueueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
            VisibilityTimeout = _visibilityTimeoutSeconds
        };

        var response = await _sqsClient.ReceiveMessageAsync(request, cancellationToken);
        var messages = response.Messages ?? [];

        if (messages.Count == 0)
            return null;

        var message = messages[0];
        _logger.LogInformation("[{Printer}] Received SQS message {MessageId}: {Body}",
            _lane.PrinterName, message.MessageId, message.Body);

        var job = JsonSerializer.Deserialize<PrintJobMessage>(message.Body);
        if (job is null)
        {
            _logger.LogError("[{Printer}] Failed to deserialize SQS message {MessageId}. Body: {Body}",
                _lane.PrinterName, message.MessageId, message.Body);
            return null;
        }

        return new JobEnvelope
        {
            Id = message.MessageId,
            Job = job,
            ReceiptHandle = message.ReceiptHandle
        };
    }

    public async Task AcknowledgeAsync(JobEnvelope envelope, bool success, string? error, CancellationToken cancellationToken)
    {
        if (success)
        {
            if (envelope.ReceiptHandle is null)
            {
                _logger.LogWarning("[{Printer}] Cannot delete SQS message {JobId}: no receipt handle",
                    _lane.PrinterName, envelope.Id);
                return;
            }

            await _sqsClient.DeleteMessageAsync(_lane.QueueUrl, envelope.ReceiptHandle, cancellationToken);
            return;
        }

        // Failure: leave the message in the queue. Visibility timeout returns it eventually,
        // and the redrive policy moves it to the DLQ after maxReceiveCount.
        _logger.LogDebug(
            "[{Printer}] Job {JobId} failed — message will return to queue after visibility timeout expires",
            _lane.PrinterName, envelope.Id);
    }
}
