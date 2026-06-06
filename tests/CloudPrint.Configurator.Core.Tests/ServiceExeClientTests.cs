using CloudPrint.Configurator.Core.Exe;

namespace CloudPrint.Configurator.Core.Tests;

public class ServiceExeClientTests
{
    private static readonly AwsCredentials Creds = new("  AKIA  ", "secret", "us-east-1");

    [Fact]
    public async Task VerifyCredentials_returns_trimmed_arn_and_sends_camelcase_stdin()
    {
        var runner = new FakeProcessRunner().Enqueue(0, "arn:aws:iam::1:user/x\n");
        var client = new ServiceExeClient(runner, "svc.exe");

        var arn = await client.VerifyCredentialsAsync(Creds);

        Assert.Equal("arn:aws:iam::1:user/x", arn);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "verify-creds" }, call.Args);
        Assert.Contains("\"accessKey\":\"AKIA\"", call.Stdin); // trimmed
        Assert.Contains("\"secretKey\":\"secret\"", call.Stdin);
        Assert.Contains("\"region\":\"us-east-1\"", call.Stdin);
    }

    [Fact]
    public async Task CreateQueue_sends_name_and_tags_and_returns_url()
    {
        var runner = new FakeProcessRunner().Enqueue(0, "https://sqs/x\n");
        var client = new ServiceExeClient(runner, "svc.exe");

        var url = await client.CreateQueueAsync(
            Creds, "cloudprint-q", new Dictionary<string, string> { ["Application"] = "cloudprint" });

        Assert.Equal("https://sqs/x", url);
        var stdin = Assert.Single(runner.Calls).Stdin!;
        Assert.Contains("\"queueName\":\"cloudprint-q\"", stdin);
        Assert.Contains("\"tags\":{\"Application\":\"cloudprint\"}", stdin);
    }

    [Fact]
    public async Task ListQueues_returns_only_https_lines()
    {
        var runner = new FakeProcessRunner().Enqueue(0, "https://a\nnot-a-url\nhttps://b\n");
        var client = new ServiceExeClient(runner, "svc.exe");

        var urls = await client.ListQueuesAsync(Creds, "cloudprint-host-");

        Assert.Equal(new[] { "https://a", "https://b" }, urls);
    }

    [Fact]
    public async Task DeleteQueue_sends_queue_url()
    {
        var runner = new FakeProcessRunner().Enqueue(0, "https://a\n");
        var client = new ServiceExeClient(runner, "svc.exe");

        await client.DeleteQueueAsync(Creds, "https://a");

        Assert.Contains("\"queueUrl\":\"https://a\"", Assert.Single(runner.Calls).Stdin);
    }

    [Fact]
    public async Task NonZeroExit_throws_with_stderr_detail()
    {
        var runner = new FakeProcessRunner().Enqueue(1, stderr: "invalid credentials");
        var client = new ServiceExeClient(runner, "svc.exe");

        var ex = await Assert.ThrowsAsync<ServiceExeException>(() => client.VerifyCredentialsAsync(Creds));
        Assert.Contains("invalid credentials", ex.Message);
    }

    [Fact]
    public async Task ListDevices_parses_serial_and_hid()
    {
        const string stdout = "Serial ports:\n  COM3\n  COM5\nHID devices:\n  VID=0922 PID=8003 Mettler Toledo\n";
        var runner = new FakeProcessRunner().Enqueue(0, stdout);
        var client = new ServiceExeClient(runner, "svc.exe");

        var inv = await client.ListDevicesAsync();

        Assert.Equal(new[] { "COM3", "COM5" }, inv.SerialPorts);
        var hid = Assert.Single(inv.HidDevices);
        Assert.Equal(0x0922, hid.Vid);
        Assert.Equal(0x8003, hid.Pid);
        Assert.Equal("Mettler Toledo", hid.Product);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "list-devices" }, call.Args);
        Assert.Null(call.Stdin); // no credentials on stdin
    }

    [Fact]
    public void ParseDeviceInventory_tolerates_empty_sections_and_missing_product()
    {
        var inv = ServiceExeClient.ParseDeviceInventory("Serial ports:\nHID devices:\n  VID=04D8 PID=0001\n");

        Assert.Empty(inv.SerialPorts);
        var hid = Assert.Single(inv.HidDevices);
        Assert.Equal(0x04D8, hid.Vid);
        Assert.Equal(0x0001, hid.Pid);
        Assert.Equal(string.Empty, hid.Product);
    }
}
