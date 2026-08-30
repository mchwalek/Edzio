namespace Edzio.Core.Transfer;

/// <summary>Describes an incoming instant-send request, shown to the receiver before they accept or decline.</summary>
/// <param name="SenderName">The sending peer's display name.</param>
/// <param name="Files">The files being offered.</param>
public sealed record TransferOffer(string SenderName, IReadOnlyList<TransferOfferFile> Files);

/// <summary>Describes a single file within a <see cref="TransferOffer"/>.</summary>
/// <param name="Name">The file's display name.</param>
/// <param name="SizeBytes">The file's size in bytes.</param>
public sealed record TransferOfferFile(string Name, long SizeBytes);
