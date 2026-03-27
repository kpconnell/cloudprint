# AWS Credentials for CloudPrint

CloudPrint needs AWS credentials with access to SQS queues. These credentials should be **tightly scoped** — they only need permission to work with `cloudprint-*` queues.

## Creating the IAM Policy

1. Open the [IAM Console](https://console.aws.amazon.com/iam/)
2. Go to **Policies** → **Create policy**
3. Switch to the **JSON** tab and paste:

```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Sid": "CloudPrintSQSAccess",
            "Effect": "Allow",
            "Action": [
                "sqs:CreateQueue",
                "sqs:ReceiveMessage",
                "sqs:DeleteMessage",
                "sqs:GetQueueUrl",
                "sqs:GetQueueAttributes"
            ],
            "Resource": "arn:aws:sqs:*:*:cloudprint-*"
        }
    ]
}
```

4. Name the policy `CloudPrintSQSAccess` and save

## Creating the IAM User

1. Go to **Users** → **Create user**
2. Name: `cloudprint-service` (or any name you prefer)
3. Do **not** enable console access
4. Attach the `CloudPrintSQSAccess` policy
5. Go to the user → **Security credentials** → **Create access key**
6. Select **Application running outside AWS**
7. Copy the **Access Key ID** and **Secret Access Key**

These two values, along with the SQS Queue URL, are what you'll provide during CloudPrint installation.

## Creating the SQS Queue

1. Open the [SQS Console](https://console.aws.amazon.com/sqs/)
2. **Create queue**
3. Name: `cloudprint-{machine-name}` (e.g., `cloudprint-warehouse-pc1`)
   - The name **must** start with `cloudprint-` to match the IAM policy
4. Type: **Standard** (not FIFO)
5. Recommended settings:
   - Visibility timeout: **60 seconds**
   - Message retention: **4 days**
   - Receive message wait time: **20 seconds** (enables long polling)
6. Optionally configure a **Dead-letter queue** to catch messages that repeatedly fail

## What These Credentials Can Do

With the policy above, the credentials can **only**:
- Receive messages from queues named `cloudprint-*`
- Delete messages from those queues
- Look up queue URLs and attributes

They **cannot**:
- Create or delete queues
- Send messages
- Access any other AWS service
- Access queues not named `cloudprint-*`

## Sharing Credentials Across Machines

You can use the same IAM user and access keys for multiple machines. Each machine points to its own queue, but the credentials are shared. IAM allows up to 2 access key pairs per user.

If you need more than 2 key pairs (e.g., for key rotation), create additional IAM users with the same policy.

## Rotating Credentials

1. Create a new access key for the IAM user
2. Re-run the CloudPrint installer on each machine with the new key
3. Delete the old access key

The installer preserves existing values — just press Enter to keep the queue URL and update only the keys.
