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
    public async Task ListDevices_parses_legacy_text_output()
    {
        // An older service exe ignores --json and prints the text form; the client must still cope.
        const string stdout = "Serial ports:\n  COM3\n  COM5\nHID devices:\n  VID=0922 PID=8003 Mettler Toledo\n";
        var runner = new FakeProcessRunner().Enqueue(0, stdout);
        var client = new ServiceExeClient(runner, "svc.exe");

        var inv = await client.ListDevicesAsync();

        Assert.Equal(new[] { "COM3", "COM5" }, inv.SerialPorts.Select(p => p.Name));
        var hid = Assert.Single(inv.HidDevices);
        Assert.Equal(0x0922, hid.Vid);
        Assert.Equal(0x8003, hid.Pid);
        Assert.Equal("Mettler Toledo", hid.Product);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "list-devices", "--json" }, call.Args);
        Assert.Null(call.Stdin); // no credentials on stdin
    }

    [Fact]
    public async Task ListDevices_parses_json_output_with_identity()
    {
        const string stdout = """
            {
              "serialPorts": [
                { "name": "COM5", "friendlyName": "USB Serial Port (COM5)", "vid": "0403", "pid": "6001", "serial": "A50285BI", "enumerator": "FTDIBUS" },
                { "name": "COM1", "friendlyName": "Communications Port (COM1)" }
              ],
              "hidDevices": [
                { "vid": "0B67", "pid": "555E", "product": "Fairbanks Scales SCB-R9000", "manufacturer": "Fairbanks", "serial": null, "usages": "008D:0020", "isScale": true },
                { "vid": "046D", "pid": "C31C", "product": "USB Keyboard", "usages": "0001:0006", "isScale": false }
              ]
            }
            """;
        var runner = new FakeProcessRunner().Enqueue(0, stdout);
        var client = new ServiceExeClient(runner, "svc.exe");

        var inv = await client.ListDevicesAsync();

        Assert.Equal(2, inv.SerialPorts.Count);
        var ftdi = inv.SerialPorts[0];
        Assert.Equal("COM5", ftdi.Name);
        Assert.Equal(0x0403, ftdi.Vid);
        Assert.Equal(0x6001, ftdi.Pid);
        Assert.Equal("A50285BI", ftdi.Serial);
        Assert.Contains("USB Serial Port", ftdi.Describe());
        Assert.Contains("VID=0403", ftdi.Describe());
        Assert.Equal("COM1  —  Communications Port (COM1)", inv.SerialPorts[1].Describe());

        Assert.Equal(2, inv.HidDevices.Count);
        var scale = inv.HidDevices[0];
        Assert.Equal(0x0B67, scale.Vid);
        Assert.True(scale.IsScale);
        Assert.Equal("008D:0020", scale.Usages);
        Assert.False(inv.HidDevices[1].IsScale);
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

    [Fact]
    public async Task TestPrint_sends_printer_name()
    {
        var runner = new FakeProcessRunner().Enqueue(0, "Sent test label to Zebra ZP500");
        var client = new ServiceExeClient(runner, "svc.exe");

        await client.TestPrintAsync("Zebra ZP500");

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "test-print" }, call.Args);
        Assert.Contains("\"printerName\":\"Zebra ZP500\"", call.Stdin);
    }

    [Fact]
    public async Task TestPrint_throws_on_failure()
    {
        var runner = new FakeProcessRunner().Enqueue(1, stderr: "Printer not found");
        var client = new ServiceExeClient(runner, "svc.exe");

        var ex = await Assert.ThrowsAsync<ServiceExeException>(() => client.TestPrintAsync("Nope"));
        Assert.Contains("Printer not found", ex.Message);
    }

    [Fact]
    public async Task TestOutput_sends_request_and_omits_null_fields()
    {
        var runner = new FakeProcessRunner().Enqueue(0, "Sent test message via sqs");
        var client = new ServiceExeClient(runner, "svc.exe");

        await client.TestOutputAsync(new OutputTestRequest
        {
            Transport = "sqs",
            QueueUrl = "https://q",
            AccessKey = "AK",
            SecretKey = "sk",
            Region = "us-east-1",
        });

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "test-output" }, call.Args);
        Assert.Contains("\"transport\":\"sqs\"", call.Stdin);
        Assert.Contains("\"queueUrl\":\"https://q\"", call.Stdin);
        Assert.DoesNotContain("webhookUrl", call.Stdin!); // null fields omitted
    }
}
