namespace Macula.Cbor;

public sealed class CborDecodeException : Exception
{
    public CborDecodeException(string message) : base(message) { }
}
