using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;
using Microsoft.Extensions.Options;

namespace CloudPrint.Service.Transport;

public class SqsJobSource : IJobSource
{
    private readonly IAmazonSQS _sqsClient;
    private readonly CloudPrintOptions _options;
    private readonly ILogger<SqsJobSource> _logger;

    public SqsJobSource(
        IAmazonSQS sqsClient,
        IOptions<CloudPrintOptions> options,
        ILogger<SqsJobSource> logger)
    {
        _sqsClient = sqsClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<JobEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var request = new ReceiveMessageRequest
        {
            QueueUrl = _options.QueueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
            VisibilityTimeout = _options.VisibilityTimeoutSeconds
        };

        var response = await _sqsClient.ReceiveMessageAsync(request, cancellationToken);
        var messages = response.Messages ?? [];

        if (messages.Count == 0)
            return null;

        var message = messages[0];
        _logger.LogInformation("Received SQS message {MessageId}: {Body}", message.MessageId, message.Body);

        var job = JsonSerializer.Deserialize<PrintJobMessage>(message.Body);
        if (job is null)
        {
            _logger.LogError("Failed to deserialize SQS message {MessageId}. Body: {Body}",
                message.MessageId, message.Body);
            return null;
        }

        return new JobEnvelope
        {
            Id = message.MessageId,
            Job = job,
            ReceiptHandle = message.ReceiptHandle
        };
    }

    public Task AcknowledgeAsync(string jobId, bool success, string? error, CancellationToken cancellationToken)
    {
        // SQS ack = delete message (handled by polling service via DeleteMessageAsync)
        // SQS failure = do nothing — visibility timeout expires and message returns to queue automatically
        if (!success)
        {
            _logger.LogDebug("Job {JobId} failed — message will return to queue after visibility timeout expires", jobId);
        }

        return Task.CompletedTask;
    }

    public async Task DeleteMessageAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        await _sqsClient.DeleteMessageAsync(_options.QueueUrl, receiptHandle, cancellationToken);
    }
}
