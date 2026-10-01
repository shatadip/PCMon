namespace PCMon.Core;

/// <summary>Fixed-size history for sparklines. Oldest values fall off the left.</summary>
public sealed class RollingBuffer(int capacity)
{
    readonly double[] _items = new double[capacity];
    int _start, _count;

    public int Capacity => _items.Length;
    public int Count => _count;

    public void Add(double value)
    {
        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = value;
            _count++;
        }
        else
        {
            _items[_start] = value;
            _start = (_start + 1) % _items.Length;
        }
    }

    public double[] ToArray()
    {
        var result = new double[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _items[(_start + i) % _items.Length];
        return result;
    }

    public double Max()
    {
        double max = 0;
        for (int i = 0; i < _count; i++)
            max = Math.Max(max, _items[(_start + i) % _items.Length]);
        return max;
    }
}
