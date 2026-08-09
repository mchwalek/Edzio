using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Edzio.Core.WebRtc;
using SIPSorcery.Net;

namespace Edzio.Benchmarks;

/// <summary>
/// Aggregate throughput across N striped associations. Loopback RTT is far below
/// the WAN case this is built for, so this measures overhead and regressions, not
/// the expected WAN gain — that is measured on the two-machine rig.
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 0, iterationCount: 5)]
public class MultiWebRtcChannelThroughputBenchmarks
{
    private const int TargetBytes = 18_500_000;
    private const int ChunkSize = 262_135;
    private static readonly int ChunkCount = TargetBytes / ChunkSize;

    [Params(1, 2, 4, 8)]
    public int LaneCount { get; set; }

    private MultiWebRtcChannel _offerer = null!;
    private MultiWebRtcChannel _answerer = null!;
    private CancellationTokenSource _cts = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(170));
        _payload = new byte[ChunkSize];
        new Random(7).NextBytes(_payload);

        _offerer = new MultiWebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer, laneCount: LaneCount);
        _answerer = new MultiWebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer, laneCount: LaneCount);

        var answererConnect = _answerer.ConnectAsync(_cts.Token);
        var offererConnect = _offerer.ConnectAsync(_cts.Token);
        await Task.WhenAll(offererConnect, answererConnect);
        await Task.WhenAll(_offerer.WaitForOpenAsync(_cts.Token), _answerer.WaitForOpenAsync(_cts.Token));
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await _offerer.DisposeAsync();
        await _answerer.DisposeAsync();
        _cts.Dispose();
    }

    [Benchmark]
    public async Task<long> Throughput()
    {
        var receiveTask = Task.Run(async () =>
        {
            long total = 0;
            for (var i = 0; i < ChunkCount; i++)
            {
                total += (await _answerer.ReceiveAsync(_cts.Token)).Length;
            }

            return total;
        }, _cts.Token);

        for (var i = 0; i < ChunkCount; i++)
        {
            await _offerer.SendAsync(_payload, _cts.Token);
        }

        await _offerer.FlushAsync(_cts.Token);
        return await receiveTask;
    }
}
