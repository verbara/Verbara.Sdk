namespace Verbara.Sdk.VoiceAi.TurnDetection.Internal;

internal sealed class RingBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _count;

    public RingBuffer(int capacity)
    {
        _buffer = new float[capacity];
    }

    public int Count => _count;
    public int Capacity => _buffer.Length;

    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _buffer[_writePos] = sample;
            _writePos = (_writePos + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }
    }

    public void CopyTo(Span<float> destination)
    {
        if (destination.Length < _count)
            throw new ArgumentException("Destination too small.", nameof(destination));

        if (_count < _buffer.Length)
        {
            _buffer.AsSpan(0, _count).CopyTo(destination);
        }
        else
        {
            var firstPart = _buffer.AsSpan(_writePos, _buffer.Length - _writePos);
            var secondPart = _buffer.AsSpan(0, _writePos);
            firstPart.CopyTo(destination);
            secondPart.CopyTo(destination[firstPart.Length..]);
        }
    }

    public void Clear()
    {
        _writePos = 0;
        _count = 0;
    }
}
