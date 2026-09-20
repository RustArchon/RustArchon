// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// The combat batch codec. The events come from a plugin on someone else's server, so what matters is that a batch not
/// exactly the shape expected is refused whole, that only a real SteamID64 ever reaches the per-player index, that
/// hostile text is cut down, and that stored data round-trips.
/// </summary>
public class CombatChunkCodecTests
{
    private const long T = 1_800_000_000_000;
    public const string Alice = "76561198000000001";
    public const string Bob = "76561198000000002";

    public static string Ev(long seq, long tMs = T, string a = Alice, bool ap = true, string v = Bob, bool vp = true, string extra = "") =>
        $"{{\"s\":{seq},\"t\":{tMs},\"k\":\"hit\",\"a\":\"{a}\",\"ap\":{(ap ? "true" : "false")},\"v\":\"{v}\",\"vp\":{(vp ? "true" : "false")},\"d\":10.5,\"vpos\":[1.0,2.0,3.0]{extra}}}";

    public static string Batch(params string[] events) => "[" + string.Join(",", events) + "]";

    private static CombatChunkCodec.InvalidCombatBatchException Refused(string json) =>
        Assert.Throws<CombatChunkCodec.InvalidCombatBatchException>(() => CombatChunkCodec.Parse(json));

    // ---- parsing -------------------------------------------------------------------------------------------

    [Fact]
    public void AWellFormedBatchIsParsedWithSequenceTimeAndPlayers()
    {
        var events = CombatChunkCodec.Parse(Batch(Ev(1), Ev(2, T + 1500)));

        Assert.Equal([1L, 2L], events.Select(e => e.Sequence).ToArray());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(T + 1500), events[1].OccurredAtUtc);
        Assert.Equal(Alice, events[0].AttackerPlayerId);
        Assert.Equal(Bob, events[0].VictimPlayerId);
    }

    [Fact]
    public void EachEventKeepsItsOwnExactJson()
    {
        var one = Ev(7);

        Assert.Equal(one, CombatChunkCodec.Parse(Batch(one))[0].RawJson);
    }

    [Fact]
    public void AnEmptyBatchIsValidAndEmpty()
    {
        Assert.Empty(CombatChunkCodec.Parse("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    public void AnythingButAnArrayOfEventObjectsIsRefused(string json)
    {
        Refused(json);
    }

    [Theory]
    [InlineData("{\"t\":1}")]                   // no sequence
    [InlineData("{\"s\":0,\"t\":1}")]           // sequence starts at 1
    [InlineData("{\"s\":-3,\"t\":1}")]
    [InlineData("{\"s\":\"1\",\"t\":1}")]       // a string is not a number
    [InlineData("{\"s\":1}")]                   // no time
    [InlineData("{\"s\":1,\"t\":-1}")]
    [InlineData("{\"s\":1,\"t\":\"x\"}")]
    [InlineData("{\"s\":1,\"t\":99999999999999999}")] // past year 9999
    public void AnEventWithNoValidSequenceOrTimeRefusesTheWholeBatch(string bad)
    {
        Refused(Batch(Ev(1), bad));
    }

    [Fact]
    public void EventsOutOfOrderOrRepeatedAreRefused()
    {
        Refused(Batch(Ev(2), Ev(1)));
        Refused(Batch(Ev(2), Ev(2)));
    }

    [Fact]
    public void ABatchOverTheEventLimitIsRefused()
    {
        var events = Enumerable.Range(1, CombatChunkCodec.MaxEventsPerBatch + 1).Select(i => Ev(i)).ToArray();

        Refused(Batch(events));
    }

    [Fact]
    public void ABatchAtTheEventLimitIsAccepted()
    {
        var events = Enumerable.Range(1, CombatChunkCodec.MaxEventsPerBatch).Select(i => Ev(i)).ToArray();

        Assert.Equal(CombatChunkCodec.MaxEventsPerBatch, CombatChunkCodec.Parse(Batch(events)).Count);
    }

    // ---- who counts as a player ----------------------------------------------------------------------------

    [Fact]
    public void AnEntityNameIsNeverIndexedAsAPlayer()
    {
        var e = CombatChunkCodec.Parse(Batch(Ev(1, a: "bear", ap: false, v: Alice, vp: true)))[0];

        Assert.Null(e.AttackerPlayerId);
        Assert.Equal(Alice, e.VictimPlayerId);
    }

    [Theory]
    [InlineData("bear")]
    [InlineData("7656119800000000x")]
    [InlineData("1; DROP TABLE")]
    [InlineData("")]
    [InlineData("123456789012345678901")]   // 21 digits
    public void APlayerFlagWithAnIdThatIsNotDigitsOfSteamLengthIsNotIndexed(string id)
    {
        var e = CombatChunkCodec.Parse(Batch(Ev(1, a: id, ap: true)))[0];

        Assert.Null(e.AttackerPlayerId);
    }

    [Fact]
    public void AnEventWhereTheFlagIsMissingIsNotIndexedEvenWithADigitId()
    {
        var json = "[{\"s\":1,\"t\":" + T + ",\"a\":\"" + Alice + "\",\"v\":\"" + Bob + "\"}]";

        var e = CombatChunkCodec.Parse(json)[0];

        Assert.Null(e.AttackerPlayerId);
        Assert.Null(e.VictimPlayerId);
    }

    // ---- compress and decode -------------------------------------------------------------------------------

    [Fact]
    public void StoredEventsRoundTripToTheFriendlyShape()
    {
        var raw = "{\"s\":5,\"t\":" + T + ",\"k\":\"death\",\"a\":\"" + Alice + "\",\"an\":\"Alice\",\"ap\":true,\"v\":\"" + Bob
            + "\",\"vn\":\"Bob\",\"vp\":true,\"w\":\"rifle.ak\",\"d\":42.5,\"dt\":\"Bullet\",\"hs\":true,\"dist\":50.0,\"apos\":[1.5,2.5,3.5],\"vpos\":[4.0,5.0,6.0]}";
        var data = CombatChunkCodec.Compress(CombatChunkCodec.Parse(Batch(raw)));

        var e = Assert.Single(CombatChunkCodec.Decode(data, 1));

        Assert.Equal(5, e.Sequence);
        Assert.Equal("death", e.Kind);
        Assert.Equal(Alice, e.AttackerId);
        Assert.Equal("Alice", e.AttackerName);
        Assert.True(e.AttackerIsPlayer);
        Assert.Equal("Bob", e.VictimName);
        Assert.Equal("rifle.ak", e.Weapon);
        Assert.Equal(42.5, e.Damage);
        Assert.Equal("Bullet", e.DamageType);
        Assert.True(e.Headshot);
        Assert.Equal(50.0, e.Distance);
        Assert.Equal([1.5, 2.5, 3.5], e.AttackerPosition);
        Assert.Equal([4.0, 5.0, 6.0], e.VictimPosition);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(T), e.OccurredAtUtc);
    }

    [Fact]
    public void MissingOptionalFieldsBecomeEmptyOrNullNotAnError()
    {
        var data = CombatChunkCodec.Compress(CombatChunkCodec.Parse(Batch("{\"s\":1,\"t\":" + T + "}")));

        var e = Assert.Single(CombatChunkCodec.Decode(data, 1));

        Assert.Equal("", e.Weapon);
        Assert.Null(e.Distance);
        Assert.Null(e.AttackerPosition);
        Assert.False(e.Headshot);
        Assert.Equal("hit", e.Kind);
    }

    [Fact]
    public void AnUnknownKindIsShownAsAHitNeverAsSomethingTheServerInvented()
    {
        var data = CombatChunkCodec.Compress(CombatChunkCodec.Parse(Batch("{\"s\":1,\"t\":" + T + ",\"k\":\"<script>\"}")));

        Assert.Equal("hit", CombatChunkCodec.Decode(data, 1)[0].Kind);
    }

    [Fact]
    public void HostileTextIsCutToAReasonableLengthBeforeItIsEverShown()
    {
        var huge = new string('x', 5000);
        var data = CombatChunkCodec.Compress(CombatChunkCodec.Parse(Batch("{\"s\":1,\"t\":" + T + ",\"an\":\"" + huge + "\",\"w\":\"" + huge + "\"}")));

        var e = Assert.Single(CombatChunkCodec.Decode(data, 1));

        Assert.Equal(100, e.AttackerName.Length);
        Assert.Equal(100, e.Weapon.Length);
    }

    [Theory]
    [InlineData("[1.0,2.0]")]
    [InlineData("[1.0,2.0,3.0,4.0]")]
    [InlineData("[1.0,\"x\",3.0]")]
    [InlineData("\"nope\"")]
    public void APositionThatIsNotThreeNumbersIsDroppedNotHalfKept(string position)
    {
        var data = CombatChunkCodec.Compress(CombatChunkCodec.Parse(Batch("{\"s\":1,\"t\":" + T + ",\"apos\":" + position + "}")));

        Assert.Null(CombatChunkCodec.Decode(data, 1)[0].AttackerPosition);
    }

    [Fact]
    public void ChunkDataIsActuallyCompressed()
    {
        var events = Enumerable.Range(1, 300).Select(i => Ev(i, T + i)).ToArray();
        var parsed = CombatChunkCodec.Parse(Batch(events));

        var data = CombatChunkCodec.Compress(parsed);

        Assert.True(data.Length < Batch(events).Length / 3, $"{data.Length} bytes for {Batch(events).Length} chars of JSON");
    }

    [Fact]
    public void ADecodeOfAnUnsupportedFormatIsRefusedRatherThanGuessed()
    {
        var data = CombatChunkCodec.Compress(CombatChunkCodec.Parse("[]"));

        Assert.Throws<InvalidOperationException>(() => CombatChunkCodec.Decode(data, 2));
    }

    [Fact]
    public void ADecompressionBombIsStoppedAtTheSizeLimit()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var chunk = Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024));
            for (var i = 0; i < 12; i++) { gzip.Write(chunk, 0, chunk.Length); } // 12 MB of spaces compresses to almost nothing
        }

        Assert.True(output.Length < 100_000);
        Assert.Throws<InvalidOperationException>(() => CombatChunkCodec.Decode(output.ToArray(), 1));
    }
}
