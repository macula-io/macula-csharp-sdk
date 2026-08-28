using Macula.Cbor;
using Macula.Content;

namespace Macula.Tests;

/// <summary>
/// Reuses macula-rust-sdk's own reference vectors for manifest.rs,
/// captured from a real `macula_manifest:create/2` via `rebar3 shell`
/// against macula-io/macula -- root hash, MCID, and the full byte-for-byte
/// `to_wire` encoding.
/// </summary>
public class ManifestTests
{
    [Fact]
    public void Even_chunk_count_matches_the_reference()
    {
        var data = "AAAABBBBCCCCD"u8.ToArray(); // 13 bytes
        var opts = new CreateOptions { Name = "test-file", ChunkSize = 4, HashAlgorithm = Algorithm.Blake3 };
        var (manifest, chunks) = ManifestBuilder.CreateWithCreated(data, opts, 0);

        Assert.Equal(4, chunks.Count);
        Assert.Equal("AAAA"u8.ToArray(), chunks[0]);
        Assert.Equal("D"u8.ToArray(), chunks[3]);

        Assert.Equal("784F87CDC9C180A21C878FC26703F9E4782F2FD2E6235048299811675E36EAC4", Convert.ToHexStringLower(manifest.RootHash).ToUpperInvariant());
        Assert.Equal("01564CC855EF538530393E36DBD4CCD216558B60F87498889890247EEB9B52B8FED7", Convert.ToHexStringLower(manifest.Mcid).ToUpperInvariant());

        Assert.Equal(0, manifest.Chunks[0].Offset);
        Assert.Equal("26C7BB3DAAAA0439EB3E5C5270E7C4DB05218D8892A0258FBD4911CEF5006D23", Convert.ToHexStringLower(manifest.Chunks[0].Hash).ToUpperInvariant());
        Assert.Equal(12, manifest.Chunks[3].Offset);
        Assert.Equal(1, manifest.Chunks[3].Size);

        var chunkMcid = ManifestBuilder.ChunkMcid(manifest, 0);
        Assert.Equal("015526C7BB3DAAAA0439EB3E5C5270E7C4DB05218D8892A0258FBD4911CEF5006D23", Convert.ToHexStringLower(chunkMcid!).ToUpperInvariant());

        ManifestBuilder.Verify(manifest, data); // does not throw
    }

    /// <summary>Odd chunk count (3) -- exercises the Merkle fold's "pair the last hash with itself" branch, which the even-count test above never touches.</summary>
    [Fact]
    public void Odd_chunk_count_matches_the_reference()
    {
        var data = "AAAABBBBCCCC"u8.ToArray(); // 12 bytes, chunk_size 4 -> exactly 3 chunks
        var opts = new CreateOptions { Name = "odd-test", ChunkSize = 4, HashAlgorithm = Algorithm.Blake3 };
        var (manifest, chunks) = ManifestBuilder.CreateWithCreated(data, opts, 0);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("50FE839CCDE80B13D7531A9C34FD856DBCBBB87D8FBD241DE6AFF2C86909CD54", Convert.ToHexStringLower(manifest.RootHash).ToUpperInvariant());
        Assert.Equal("0156589728C90DB0138CA87E4E500A61812C64D30C3BE325184A761F20CA04BC86FB", Convert.ToHexStringLower(manifest.Mcid).ToUpperInvariant());

        ManifestBuilder.Verify(manifest, data); // does not throw

        var ex = Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Verify(manifest, "AAAABBBBWRONG"u8.ToArray()));
        Assert.Equal(VerifyError.SizeMismatch.ToString(), ex.Message);
    }

    [Fact]
    public void Verify_rejects_tampered_content_of_the_same_size()
    {
        var data = "AAAABBBBCCCC"u8.ToArray();
        var opts = new CreateOptions { ChunkSize = 4 };
        var (manifest, _) = ManifestBuilder.CreateWithCreated(data, opts, 0);

        var ex = Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Verify(manifest, "AAAABBBBCCCX"u8.ToArray()));
        Assert.Equal(VerifyError.RootHashMismatch.ToString(), ex.Message);
    }

    /// <summary>
    /// The full manifest map as it's actually sent in a
    /// `_content.put_manifest` CALL payload -- proves `name` really is
    /// bytes on the wire, not text.
    /// </summary>
    [Fact]
    public void ToWire_matches_the_reference_byte_for_byte()
    {
        var data = "AAAABBBBCCCC"u8.ToArray();
        var opts = new CreateOptions { Name = "odd-test", ChunkSize = 4, HashAlgorithm = Algorithm.Blake3 };
        var (manifest, _) = ManifestBuilder.CreateWithCreated(data, opts, 1_787_892_082); // 0x6A911172

        var wire = ManifestBuilder.ToWire(manifest);
        var encoded = CborCodec.Encode(wire);

        Assert.Equal(
            "AA646D63696458220156589728C90DB0138CA87E4E500A61812C64D30C3BE325184A761F20CA04BC86FB646E616D65486F64642D746573746473697A650C666368756E6B7383A46468617368582026C7BB3DAAAA0439EB3E5C5270E7C4DB05218D8892A0258FBD4911CEF5006D236473697A650465696E64657800666F666673657400A464686173685820255EC90F561EDA98B1E5E3EFA56B7B477086E273CD07CC4F780A646D052726446473697A650465696E64657801666F666673657404A464686173685820A83CE6EC6760EB7F66D3D7BBC84D1AAC3BEF0948074F8ED21423D825AE8821726473697A650465696E64657802666F66667365740867637265617465641A6A9111726776657273696F6E0169726F6F745F68617368582050FE839CCDE80B13D7531A9C34FD856DBCBBB87D8FBD241DE6AFF2C86909CD546A6368756E6B5F73697A65046B6368756E6B5F636F756E74036E686173685F616C676F726974686D66626C616B6533",
            Convert.ToHexStringLower(encoded).ToUpperInvariant());

        // Round-trip through FromWire.
        var decoded = CborCodec.Decode(encoded);
        var parsed = ManifestBuilder.FromWire(decoded);
        Assert.Equal(manifest, parsed);
    }

    [Fact]
    public void FromWire_rejects_a_missing_field()
    {
        var value = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("mcid"), Value.Bytes(new byte[34])) });
        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(value));
        Assert.Equal(FromWireError.MissingField, ex.Kind);
        Assert.Equal("version", ex.Field);
    }

    [Fact]
    public void Algorithm_from_name_defaults_to_blake3()
    {
        Assert.Equal(Algorithm.Blake3, AlgorithmExtensions.FromName("blake3"));
        Assert.Equal(Algorithm.Sha256, AlgorithmExtensions.FromName("sha256"));
        Assert.Equal(Algorithm.Blake3, AlgorithmExtensions.FromName("something-unknown"));
    }

    [Fact]
    public void Empty_data_produces_zero_chunks()
    {
        var (manifest, chunks) = ManifestBuilder.CreateWithCreated(Array.Empty<byte>(), new CreateOptions(), 0);
        Assert.Empty(chunks);
        Assert.Equal(0, manifest.ChunkCount);
    }
}
