using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Devices;

namespace CloudPrint.Service.Publishing;

/// <summary>
/// Publishes readings to an SQS queue via SendMessage, reusing the shared <see cref="IAmazonSQS"/>
/// client. Requires the <c>sqs:SendMessage</c> IAM action on the output queue.
/// </summary>
public class SqsReadingPublisher : IReadingPublisher
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly ILogger<SqsReadingPublisher> _logger;

    public SqsReadingPublisher(IAmazonSQS sqs, string queueUrl, ILogger<SqsReadingPublisher> logger)
    {
        _sqs = sqs;
        _queueUrl = queueUrl;
        _logger = logger;
    }

    public async Task PublishAsync(DeviceReading reading, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_queueUrl))
            throw new InvalidOperationException("Device output QueueUrl is not configured");

        var body = JsonSerializer.Serialize(reading);
        await _sqs.SendMessageAsync(new SendMessageRequest(_queueUrl, body), cancellationToken);

        _logger.LogDebug("[{DeviceId}] published reading {ReadingId} to {Queue}",
            reading.DeviceId, reading.ReadingId, _queueUrl);
    }
}
