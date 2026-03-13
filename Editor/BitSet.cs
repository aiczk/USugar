using System;

public struct BitSet
{
    ulong[] _bits;

    public static BitSet Create(int capacity)
    {
        return new BitSet { _bits = new ulong[(capacity + 63) >> 6] };
    }

    public void Set(int index) => _bits[index >> 6] |= 1UL << (index & 63);
    public void Clear(int index) => _bits[index >> 6] &= ~(1UL << (index & 63));
    public bool Get(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;

    public void UnionWith(BitSet other)
    {
        for (int i = 0; i < _bits.Length; i++)
            _bits[i] |= other._bits[i];
    }

    public void ExceptWith(BitSet other)
    {
        for (int i = 0; i < _bits.Length; i++)
            _bits[i] &= ~other._bits[i];
    }

    public bool SetEquals(BitSet other)
    {
        for (int i = 0; i < _bits.Length; i++)
            if (_bits[i] != other._bits[i]) return false;
        return true;
    }

    public BitSet Copy()
    {
        var clone = new BitSet { _bits = new ulong[_bits.Length] };
        Array.Copy(_bits, clone._bits, _bits.Length);
        return clone;
    }

    public bool IsValid => _bits != null;
}
