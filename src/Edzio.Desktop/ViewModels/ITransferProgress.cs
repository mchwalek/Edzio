namespace Edzio.Desktop.ViewModels;

/// <summary>
/// The transfer-progress properties shared by <see cref="SendViewModel"/> and
/// <see cref="ReceiveViewModel"/>, allowing both to bind to a single
/// <c>TransferProgressView</c> control instead of duplicating its XAML.
/// </summary>
public interface ITransferProgress
{
    /// <summary>Fraction complete, 0.0–1.0, for binding to a <c>ProgressBar</c>.</summary>
    double ProgressValue { get; }

    /// <summary>Current smoothed transfer rate, formatted for display (e.g. "2.4 MB/s").</summary>
    string SpeedText { get; }

    /// <summary>Estimated time remaining, formatted for display (e.g. "1:32 remaining").</summary>
    string RemainingText { get; }

    /// <summary>Bytes transferred so far vs. total, formatted for display (e.g. "12.3 MB / 45.0 MB").</summary>
    string TransferredText { get; }
}
