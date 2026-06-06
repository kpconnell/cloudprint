namespace CloudPrint.Service.Devices.Parsing;

/// <summary>
/// Parses one delimited serial frame into a <see cref="DeviceReading"/>. Pure and hardware-free:
/// the reader owns I/O, the parser owns interpretation. Returns null for partial/unrecognized
/// frames and never throws on malformed input.
/// </summary>
public interface ISerialLineParser
{
    DeviceReading? TryParse(string line);
}
