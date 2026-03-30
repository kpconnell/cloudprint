# AWS Credentials for CloudPrint

CloudPrint needs AWS credentials with access to SQS queues. These credentials should be **tightly scoped** — they only need permission to work with `cloudprint-*` queues.

## 1. Create an IAM Policy

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
                "sqs:SetQueueAttributes",
                "sqs:GetQueueAttributes",
                "sqs:GetQueueUrl",
                "sqs:ReceiveMessage",
                "sqs:DeleteMessage"
            ],
            "Resource": "arn:aws:sqs:*:*:cloudprint-*"
        },
        {
            "Sid": "CloudPrintCredentialVerification",
            "Effect": "Allow",
            "Action": "sts:GetCallerIdentity",
            "Resource": "*"
        }
    ]
}
```

4. Name the policy `CloudPrintSQSAccess` and save

### Why each permission is needed

| Action | Purpose |
|--------|---------|
| `sqs:CreateQueue` | Installer auto-creates the main queue and dead-letter queue |
| `sqs:SetQueueAttributes` | Sets the redrive policy (DLQ) on an existing queue |
| `sqs:GetQueueAttributes` | Reads the DLQ ARN to configure the redrive policy |
| `sqs:GetQueueUrl` | Looks up the queue URL when the queue already exists |
| `sqs:ReceiveMessage` | Long-polls the queue for print jobs |
| `sqs:DeleteMessage` | Removes a message after a job prints successfully |
| `sts:GetCallerIdentity` | Validates credentials during installation |

## 2. Create an IAM User

1. Go to **Users** → **Create user**
2. Name: `cloudprint-service` (or any name you prefer)
3. Do **not** enable console access
4. Attach the `CloudPrintSQSAccess` policy directly
5. Go to the user → **Security credentials** → **Create access key**
6. Select **Application running outside AWS**
7. Copy the **Access Key ID** and **Secret Access Key**

These two values are what you'll provide during CloudPrint installation. The installer uses them to verify connectivity, create queues, and poll for print jobs.

> **Note:** You do not need to create SQS queues manually. The installer creates the main queue and dead-letter queue automatically.

## What These Credentials Can Do

With the policy above, the credentials can **only**:
- Create and configure queues named `cloudprint-*`
- Receive and delete messages from those queues
- Look up queue URLs and attributes
- Verify their own identity (STS)

They **cannot**:
- Send messages to any queue
- Delete queues
- Access queues not named `cloudprint-*`
- Access any other AWS service (S3, EC2, Lambda, etc.) beyond the read-only `sts:GetCallerIdentity` call used during install

## Sharing Credentials Across Machines

You can use the same IAM user and access keys for multiple machines. Each machine points to its own queue, but the credentials are shared. IAM allows up to 2 access key pairs per user.

If you need more than 2 key pairs (e.g., for key rotation), create additional IAM users with the same policy.

## Rotating Credentials

1. Create a new access key for the IAM user
2. Re-run the CloudPrint installer on each machine with the new key
3. Delete the old access key

The installer preserves existing values — just press Enter to keep the queue URL and update only the keys.

## AWS CLI Alternative

If you prefer the CLI over the console:

```bash
# Create the policy
aws iam create-policy \
  --policy-name CloudPrintSQSAccess \
  --policy-document file://docs/cloudprint-iam-policy.json

# Create the user
aws iam create-user --user-name cloudprint-service

# Attach the policy (replace ACCOUNT_ID with your AWS account ID)
aws iam attach-user-policy \
  --user-name cloudprint-service \
  --policy-arn arn:aws:iam::ACCOUNT_ID:policy/CloudPrintSQSAccess

# Create access keys
aws iam create-access-key --user-name cloudprint-service
```

The `create-access-key` command outputs the Access Key ID and Secret Access Key. Save these — the secret is only shown once.
