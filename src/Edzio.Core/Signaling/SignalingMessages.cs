namespace Edzio.Core.Signaling;

public static class SignalingMethods
{
    public const string RegisterReceiver = "RegisterReceiver";
    public const string JoinAsSender = "JoinAsSender";
    public const string SendOffer = "SendOffer";
    public const string SendAnswer = "SendAnswer";
    public const string SendIceCandidate = "SendIceCandidate";
}

public static class SignalingEvents
{
    public const string OfferReceived = "OfferReceived";
    public const string AnswerReceived = "AnswerReceived";
    public const string IceCandidateReceived = "IceCandidateReceived";
    public const string PeerJoined = "PeerJoined";
    public const string PeerDisconnected = "PeerDisconnected";
}
