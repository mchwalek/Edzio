using Edzio.Core.Signaling;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Signaling;

public class IndexedSignalingClientTests
{
    [Fact]
    public async Task SendOfferAsync_TagsThePayloadWithTheLaneIndex()
    {
        var inner = new FakeSignalingClient();
        await using var lane3 = new IndexedSignalingClient(inner, 3);

        await lane3.SendOfferAsync("v=0 fake sdp");

        inner.SentOffers.Should().ContainSingle();
        inner.SentOffers[0].Should().Contain("\"edzioLane\":3");
        inner.SentOffers[0].Should().NotBe("v=0 fake sdp", "the payload must be wrapped");
    }

    [Fact]
    public async Task OfferReceived_IsRaisedOnlyOnTheMatchingLane_AndUnwrapped()
    {
        var inner = new FakeSignalingClient();
        await using var lane0 = new IndexedSignalingClient(inner, 0);
        await using var lane1 = new IndexedSignalingClient(inner, 1);

        string? seenByLane0 = null;
        string? seenByLane1 = null;
        lane0.OfferReceived += (_, sdp) => seenByLane0 = sdp;
        lane1.OfferReceived += (_, sdp) => seenByLane1 = sdp;

        // Build the wire payload the way a peer's lane 1 would.
        var sender = new FakeSignalingClient();
        await using var remoteLane1 = new IndexedSignalingClient(sender, 1);
        await remoteLane1.SendOfferAsync("v=0 lane one");

        inner.SimulateOfferReceived(sender.SentOffers[0]);

        seenByLane1.Should().Be("v=0 lane one");
        seenByLane0.Should().BeNull("lane 0 must not see lane 1's offer");
    }

    [Fact]
    public async Task IceCandidateReceived_RoutesByLane()
    {
        var inner = new FakeSignalingClient();
        await using var lane2 = new IndexedSignalingClient(inner, 2);
        await using var lane5 = new IndexedSignalingClient(inner, 5);

        var lane2Seen = new List<string>();
        var lane5Seen = new List<string>();
        lane2.IceCandidateReceived += (_, c) => lane2Seen.Add(c);
        lane5.IceCandidateReceived += (_, c) => lane5Seen.Add(c);

        var sender = new FakeSignalingClient();
        await using var remoteLane5 = new IndexedSignalingClient(sender, 5);
        await remoteLane5.SendIceCandidateAsync("{\"candidate\":\"a\"}");
        await remoteLane5.SendIceCandidateAsync("{\"candidate\":\"b\"}");

        foreach (var wire in sender.SentIceCandidates)
        {
            inner.SimulateIceCandidateReceived(wire);
        }

        lane5Seen.Should().Equal("{\"candidate\":\"a\"}", "{\"candidate\":\"b\"}");
        lane2Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task UnwrappedPayloads_AreDroppedRatherThanMisrouted()
    {
        // The LAN endpoint advertisement rides the same ICE relay but carries no lane
        // tag. It must not be delivered to any lane as if it were a candidate.
        var inner = new FakeSignalingClient();
        await using var lane0 = new IndexedSignalingClient(inner, 0);

        var seen = new List<string>();
        lane0.IceCandidateReceived += (_, c) => seen.Add(c);

        inner.SimulateIceCandidateReceived("{\"edzioLanEndpoint\":{\"Port\":1234}}");
        inner.SimulateIceCandidateReceived("not json at all");

        seen.Should().BeEmpty();
    }

    [Fact]
    public async Task InboundEventsArrivingBeforeSubscription_AreReplayedOnSubscribe()
    {
        // WebRtcChannel subscribes inside ConnectAsync, which for lane N starts after
        // lane 0's. A fast peer can deliver lane N's offer into that window.
        var inner = new FakeSignalingClient();
        await using var lane0 = new IndexedSignalingClient(inner, 0);

        var sender = new FakeSignalingClient();
        await using var remoteLane0 = new IndexedSignalingClient(sender, 0);
        await remoteLane0.SendOfferAsync("v=0 early");
        await remoteLane0.SendIceCandidateAsync("{\"candidate\":\"early\"}");

        inner.SimulateOfferReceived(sender.SentOffers[0]);
        inner.SimulateIceCandidateReceived(sender.SentIceCandidates[0]);

        // Subscribe only now.
        string? offer = null;
        var candidates = new List<string>();
        lane0.OfferReceived += (_, s) => offer = s;
        lane0.IceCandidateReceived += (_, c) => candidates.Add(c);

        offer.Should().Be("v=0 early");
        candidates.Should().Equal("{\"candidate\":\"early\"}");
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDisposeTheSharedInnerClient()
    {
        var inner = new DisposeTrackingSignalingClient();
        var lane0 = new IndexedSignalingClient(inner, 0);

        await lane0.DisposeAsync();

        inner.Disposed.Should().BeFalse(
            "the inner client is shared across all lanes and owned by the caller");
    }

    private sealed class DisposeTrackingSignalingClient : ISignalingClient
    {
        public bool Disposed { get; private set; }

        public Task ConnectAsync(string serverUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> RegisterAsReceiverAsync(CancellationToken ct = default) => Task.FromResult("ABCDEF");
        public Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default) => Task.FromResult(true);
        public Task SendOfferAsync(string sdp, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAnswerAsync(string sdp, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default) => Task.CompletedTask;

        public SignalingConnectionState ConnectionState => SignalingConnectionState.Disconnected;
        public event EventHandler<SignalingConnectionState> ConnectionStateChanged { add { } remove { } }
        public Task WaitForConnectedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<string> OfferReceived = delegate { };
        public event EventHandler<string> AnswerReceived = delegate { };
        public event EventHandler<string> IceCandidateReceived = delegate { };
        public event EventHandler PeerJoined = delegate { };
        public event EventHandler PeerDisconnected = delegate { };

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
