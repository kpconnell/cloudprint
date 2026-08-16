using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace CloudPrint.Service.Devices.Commands;

/// <summary>
/// Long-polls one SQS queue for device commands. One queue per station serves all its devices; the
/// message body names the device. Malformed messages are logged and deleted (they would never succeed).
/// </summary>
public sealed class SqsDeviceCommandSource : IDeviceCommandSource
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly ILogger<SqsDeviceCommandSource> _logger;

    public SqsDeviceCommandSource(IAmazonSQS sqs, string queueUrl, ILogger<SqsDeviceCommandSource> logger)
    {
        _sqs = sqs;
        _queueUrl = queueUrl;
        _logger = logger;
    }

    public async Task<DeviceCommandEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
            VisibilityTimeout = 60
        }, cancellationToken);

        var messages = response.Messages ?? [];
        if (messages.Count == 0)
            return null;

        var message = messages[0];
        _logger.LogInformation("Received device command {MessageId}: {Body}", message.MessageId, message.Body);

        DeviceCommandMessage? command = null;
        try { command = JsonSerializer.Deserialize<DeviceCommandMessage>(message.Body, JsonOptions); }
        catch (JsonException ex) { _logger.LogError(ex, "Device command {MessageId} is not valid JSON", message.MessageId); }

        if (command is null || command.TargetDevice.Length == 0)
        {
            _logger.LogError("Device command {MessageId} has no target device; deleting. Body: {Body}", message.MessageId, message.Body);
            await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, cancellationToken);
            return null;
        }

        command.Id ??= message.MessageId;
        return new DeviceCommandEnvelope { Id = message.MessageId, Message = command, ReceiptHandle = message.ReceiptHandle };
    }

    public Task AcknowledgeAsync(DeviceCommandEnvelope envelope, CancellationToken cancellationToken) =>
        envelope.ReceiptHandle is null
            ? Task.CompletedTask
            : _sqs.DeleteMessageAsync(_queueUrl, envelope.ReceiptHandle, cancellationToken);
}
