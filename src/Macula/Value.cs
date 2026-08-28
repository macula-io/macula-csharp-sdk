using System.Text;

namespace Macula;

/// <summary>
/// A value in the Macula wire protocol's CBOR value space. Mirrors the
/// deterministic encoder's own variant set (macula's
/// native/macula_cbor_nif/src/deterministic.rs) -- not general CBOR.
/// </summary>
public abstract record Value
{
    private Value() { }

    public sealed record UIntValue(ulong Value) : Value;

    /// <summary>Negative integer; the actual value is -1 - NMinusOne, covering down to -(2^64).</summary>
    public sealed record NegIntValue(ulong NMinusOne) : Value;

    public sealed record BytesValue(byte[] Value) : Value;

    /// <summary>
    /// Wire major type 3. Stored as raw bytes, not validated as UTF-8 on
    /// decode -- matches the Erlang encoder's own leniency.
    /// </summary>
    public sealed record TextValue(byte[] Utf8) : Value;

    public sealed record ListValue(IReadOnlyList<Value> Items) : Value;

    public sealed record MapValue(IReadOnlyList<KeyValuePair<Value, Value>> Entries) : Value
    {
        /// <summary>
        /// Returns a new map with <paramref name="key"/> set to
        /// <paramref name="value"/> -- replacing an existing entry for that
        /// key rather than appending a duplicate. This value model's Map is
        /// a plain ordered list of pairs, not a real map, so appending
        /// blindly on top of a sentinel field the envelope already set
        /// (e.g. <c>base()</c>'s <c>call_id =&gt; null</c>) would silently
        /// produce a wire-invalid map with two entries for the same key
        /// instead of overriding it.
        /// </summary>
        public MapValue WithField(string key, Value value)
        {
            var entries = new List<KeyValuePair<Value, Value>>(Entries.Count + 1);
            var replaced = false;
            foreach (var entry in Entries)
            {
                if (!replaced && entry.Key is TextValue t && Encoding.UTF8.GetString(t.Utf8) == key)
                {
                    entries.Add(new KeyValuePair<Value, Value>(Text(key), value));
                    replaced = true;
                }
                else
                {
                    entries.Add(entry);
                }
            }
            if (!replaced)
            {
                entries.Add(new KeyValuePair<Value, Value>(Text(key), value));
            }
            return new MapValue(entries);
        }

        /// <summary>Returns this map with the given keys removed, if present.</summary>
        public MapValue Without(params string[] keys)
        {
            var keySet = new HashSet<string>(keys);
            var entries = Entries
                .Where(entry => entry.Key is not TextValue t || !keySet.Contains(Encoding.UTF8.GetString(t.Utf8)))
                .ToList();
            return new MapValue(entries);
        }

        /// <summary>Looks up a field by its text key, or null if absent.</summary>
        public Value? Get(string key)
        {
            foreach (var entry in Entries)
            {
                if (entry.Key is TextValue t && Encoding.UTF8.GetString(t.Utf8) == key)
                {
                    return entry.Value;
                }
            }
            return null;
        }
    }

    public sealed record NullValue : Value;

    /// <summary>Always round-trips through binary64 on encode, regardless of source width.</summary>
    public sealed record FloatValue(double Value) : Value;

    public static readonly Value Null = new NullValue();

    public static Value UInt(ulong value) => new UIntValue(value);

    public static Value Int(long value) => value >= 0
        ? new UIntValue((ulong)value)
        : new NegIntValue((ulong)(-1 - value));

    /// <summary>For the full negative range down to -(2^64), where a plain long can't reach.</summary>
    public static Value NegInt(ulong nMinusOne) => new NegIntValue(nMinusOne);

    public static Value Bytes(byte[] value) => new BytesValue(value);

    public static Value Text(string value) => new TextValue(Encoding.UTF8.GetBytes(value));

    public static Value TextBytes(byte[] utf8) => new TextValue(utf8);

    public static Value List(IReadOnlyList<Value> items) => new ListValue(items);

    public static Value Map(IReadOnlyList<KeyValuePair<Value, Value>> entries) => new MapValue(entries);

    public static Value Float(double value) => new FloatValue(value);

    public bool IsNull => this is NullValue;

    /// <summary>Decodes this value's bytes as UTF-8. Throws if this isn't a Text/Bytes value.</summary>
    public string AsText() => this switch
    {
        TextValue t => Encoding.UTF8.GetString(t.Utf8),
        BytesValue b => Encoding.UTF8.GetString(b.Value),
        _ => throw new InvalidOperationException($"{GetType().Name} has no text representation"),
    };

    public byte[] AsBytes() => this switch
    {
        BytesValue b => b.Value,
        TextValue t => t.Utf8,
        _ => throw new InvalidOperationException($"{GetType().Name} has no byte representation"),
    };

    public long AsInt() => this switch
    {
        UIntValue u when u.Value <= long.MaxValue => (long)u.Value,
        NegIntValue n when n.NMinusOne <= long.MaxValue => -1L - (long)n.NMinusOne,
        UIntValue or NegIntValue => throw new OverflowException("value does not fit in a signed 64-bit integer"),
        _ => throw new InvalidOperationException($"{GetType().Name} is not an integer"),
    };
}
