// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// The plugin's <c>archon.map.status</c> and <c>archon.map.monuments</c> replies. A reply the Worker does not fully
/// understand is never published, an older plugin that lacks the upload block still parses, and the monument list is
/// passed on as the exact JSON the plugin sent.
/// </summary>
public class ArchonMapParserTests
{
    private static string Status(
        string world = "{\"known\":true,\"size\":4500,\"seed\":317670913}", string file = "\"map_4500_317670913.png\"",
        string exists = "true", string bytes = "24113743", string? upload = null, string envelope = "1", string ok = "true")
    {
        var uploadPart = upload is null ? "" : ",\"upload\":" + upload;
        return "{\"v\":" + envelope + ",\"ok\":" + ok + ",\"data\":{\"world\":" + world + ",\"file\":" + file + ",\"exists\":" + exists
            + ",\"bytes\":" + bytes + ",\"rendering\":false,\"auto\":true,\"lastRender\":null" + uploadPart + "}}";
    }

    private static ArchonMapStatus Parse(string reply)
    {
        Assert.True(ArchonMapParser.TryParseStatus(reply, out var status, out var failure), failure);
        return status!;
    }

    private static string Failure(string? reply)
    {
        Assert.False(ArchonMapParser.TryParseStatus(reply, out var status, out var failure));
        Assert.Null(status);
        return failure!;
    }

    [Fact]
    public void AKnownWorldWithAPictureParses()
    {
        var status = Parse(Status());

        Assert.Equal(new ArchonMapStatus(true, 4500, 317670913, "map_4500_317670913.png", true, 24113743, "idle"), status);
    }

    [Fact]
    public void ABuildThatLacksTheUploadBlockIsIdle()
    {
        Assert.Equal("idle", Parse(Status(upload: null)).UploadState);
        Assert.Equal("idle", Parse(Status(upload: "null")).UploadState);
    }

    [Theory]
    [InlineData("uploading")]
    [InlineData("done")]
    [InlineData("failed")]
    public void TheUploadStateIsCarried(string state)
    {
        var status = Parse(Status(upload: "{\"state\":\"" + state + "\",\"file\":\"x\",\"bytes\":1,\"atMs\":1}"));

        Assert.Equal(state, status.UploadState);
    }

    [Fact]
    public void AWorldThatHasNotLoadedYetParsesAsUnknown()
    {
        var status = Parse(Status(world: "{\"known\":false,\"size\":0,\"seed\":0}", file: "\"\"", exists: "false", bytes: "0"));

        Assert.False(status.WorldKnown);
        Assert.False(status.Exists);
    }

    [Fact]
    public void ExtraFieldsFromANewerPluginAreIgnored()
    {
        var reply = "{\"v\":1,\"ok\":true,\"x\":1,\"data\":{\"world\":{\"known\":true,\"size\":1000,\"seed\":1,\"extra\":2},\"file\":\"f\",\"exists\":false,\"bytes\":0,\"future\":{}}}";

        Assert.Equal(1000, Parse(reply).WorldSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Unknown command: archon.map.status")]
    [InlineData("[]")]
    [InlineData("null")]
    public void AnythingThatIsNotAnEnvelopeObjectFails(string? reply)
    {
        Assert.NotEmpty(Failure(reply));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("\"1\"")]
    public void AnUnsupportedEnvelopeVersionFails(string envelope)
    {
        Assert.Contains("envelope", Failure(Status(envelope: envelope)));
    }

    [Fact]
    public void AnErrorReplyFailsWithItsReason()
    {
        Assert.Contains("usage", Failure("{\"v\":1,\"ok\":false,\"err\":\"usage\",\"message\":\"x\"}"));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void OnlyABooleanTrueOkCounts(string ok)
    {
        Assert.NotEmpty(Failure(Status(ok: ok)));
    }

    [Theory]
    [InlineData("{\"known\":\"yes\",\"size\":4500,\"seed\":1}")]      // known must be a real boolean
    [InlineData("{\"known\":true,\"size\":\"4500\",\"seed\":1}")]     // size must be a number
    [InlineData("{\"known\":true,\"size\":-1,\"seed\":1}")]
    [InlineData("{\"known\":true,\"size\":99999999999,\"seed\":1}")]  // too big for an int
    [InlineData("{\"known\":true,\"size\":4500,\"seed\":-5}")]
    [InlineData("{\"known\":true,\"size\":4500}")]                     // no seed
    [InlineData("[]")]
    public void ABadWorldBlockFails(string world)
    {
        Assert.NotEmpty(Failure(Status(world: world)));
    }

    [Theory]
    [InlineData("1", "true", "5")]                  // file not a string
    [InlineData("\"f\"", "\"true\"", "5")]          // exists not a boolean
    [InlineData("\"f\"", "true", "\"5\"")]          // bytes not a number
    [InlineData("\"f\"", "true", "-5")]             // negative size
    public void ABadFileBlockFails(string file, string exists, string bytes)
    {
        Assert.NotEmpty(Failure(Status(file: file, exists: exists, bytes: bytes)));
    }

    [Fact]
    public void AnUploadBlockWithoutAStateFails()
    {
        Assert.Contains("state", Failure(Status(upload: "{\"file\":\"x\"}")));
    }

    // ---- monuments -----------------------------------------------------------------------------------------------

    private static string Monuments(string array) => "{\"v\":1,\"ok\":true,\"data\":{\"format\":1,\"size\":4500,\"monuments\":" + array + "}}";

    [Fact]
    public void MonumentsArePassedOnAsTheExactRawJson()
    {
        var array = "[{\"n\":\"Ünï \\\"q\\\"\",\"x\":1.5,\"y\":0.0,\"z\":-3.0},{\"n\":\"Launch Site\",\"x\":10.0,\"y\":2.0,\"z\":3.0}]";

        Assert.True(ArchonMapParser.TryParseMonuments(Monuments(array), out var json, out var failure), failure);

        Assert.Equal(array, json);
    }

    [Fact]
    public void AnEmptyMonumentListIsValid()
    {
        Assert.True(ArchonMapParser.TryParseMonuments(Monuments("[]"), out var json, out _));
        Assert.Equal("[]", json);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    public void MonumentsThatAreNotAnArrayFail(string array)
    {
        Assert.False(ArchonMapParser.TryParseMonuments(Monuments(array), out var json, out var failure));
        Assert.Null(json);
        Assert.Contains("monuments", failure);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[{\"x\":1,\"y\":1,\"z\":1}]")]                        // no name
    [InlineData("[{\"n\":1,\"x\":1,\"y\":1,\"z\":1}]")]                // name not text
    [InlineData("[{\"n\":\"A\",\"x\":\"1\",\"y\":1,\"z\":1}]")]        // position not numeric
    [InlineData("[{\"n\":\"A\",\"x\":1,\"y\":1}]")]                    // missing coordinate
    public void AMonumentWithoutANameAndPositionFailsTheWholeList(string array)
    {
        Assert.False(ArchonMapParser.TryParseMonuments(Monuments(array), out var json, out var failure));
        Assert.Null(json);
        Assert.Contains("monument", failure);
    }

    [Fact]
    public void AMonumentsErrorReplyFails()
    {
        Assert.False(ArchonMapParser.TryParseMonuments("{\"v\":1,\"ok\":false,\"err\":\"unavailable\"}", out _, out var failure));
        Assert.Contains("unavailable", failure);
    }

    [Fact]
    public void GarbageInsteadOfMonumentsFails()
    {
        Assert.False(ArchonMapParser.TryParseMonuments("Unknown command", out _, out var failure));
        Assert.Contains("JSON", failure);
    }
}
