namespace CloudPrint.Service.Devices.Framing;

/// <summary>
/// Cuts an incoming byte stream into device frames according to <see cref="FramingOptions"/>.
/// Pure and hardware-free: readers push whatever bytes the channel returned and drain completed
/// frames; on a read timeout they call <see cref="Flush"/> so idle-gap framing can close a frame.
/// Never throws on odd input; oversized frames are emitted as-is so nothing is silently lost.
/// </summary>
public sealed class FrameAssembler
{
    private readonly FramingOptions _o;
    private readonly List<byte> _buf = new();
    private readonly Queue<byte[]> _ready = new();
    private bool _inFrame; // Delimited mode with a Start sequence: are we inside start..end?

    public FrameAssembler(FramingOptions options)
    {
        _o = options;
        _inFrame = options.Mode != FrameMode.Delimited || options.Start.Length == 0;
    }

    public int PendingBytes => _buf.Count;

    /// <summary>Feed bytes; completed frames become available via <see cref="TryDequeue"/>.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            switch (_o.Mode)
            {
                case FrameMode.Line:
                    _buf.Add(b);
                    if (EndsWith(_o.Terminator))
                    {
                        _buf.RemoveRange(_buf.Count - _o.Terminator.Length, _o.Terminator.Length);
                        Emit();
                    }
                    else if (_buf.Count >= _o.MaxFrameBytes) Emit();
                    break;

                case FrameMode.Delimited:
                    if (!_inFrame)
                    {
                        // Hunting for the start sequence: keep only a sliding window of its length.
                        _buf.Add(b);
                        if (_buf.Count > _o.Start.Length) _buf.RemoveAt(0);
                        if (EndsWith(_o.Start)) _inFrame = true;
                        break;
                    }
                    _buf.Add(b);
                    if (_o.End.Length > 0 && EndsWith(_o.End))
                    {
                        Emit();
                        _inFrame = _o.Start.Length == 0;
                    }
                    else if (_buf.Count >= _o.MaxFrameBytes)
                    {
                        Emit(); // runaway frame (missed end): hand it over rather than grow forever
                        _inFrame = _o.Start.Length == 0;
                    }
                    break;

                case FrameMode.Idle:
                    _buf.Add(b);
                    if (_buf.Count >= _o.MaxFrameBytes) Emit();
                    break;
            }
        }
    }

    /// <summary>
    /// Called when the channel went quiet (read timeout). In Idle mode the pending bytes become a frame.
    /// In the other modes pending bytes are kept — a frame may simply be arriving slowly.
    /// </summary>
    public void Flush()
    {
        if (_o.Mode == FrameMode.Idle && _buf.Count > 0)
            Emit();
    }

    /// <summary>Discard partial state (e.g. after a reconnect).</summary>
    public void Reset()
    {
        _buf.Clear();
        _ready.Clear();
        _inFrame = _o.Mode != FrameMode.Delimited || _o.Start.Length == 0;
    }

    public bool TryDequeue(out byte[] frame)
    {
        if (_ready.Count > 0) { frame = _ready.Dequeue(); return true; }
        frame = Array.Empty<byte>();
        return false;
    }

    private void Emit()
    {
        if (_buf.Count > 0)
            _ready.Enqueue(_buf.ToArray());
        _buf.Clear();
    }

    private bool EndsWith(byte[] seq)
    {
        if (seq.Length == 0 || _buf.Count < seq.Length) return false;
        for (var i = 0; i < seq.Length; i++)
            if (_buf[_buf.Count - seq.Length + i] != seq[i]) return false;
        return true;
    }
}
