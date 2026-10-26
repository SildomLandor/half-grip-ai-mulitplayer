using System;
using System.Net;

namespace HalfGripMultiplayer;

internal enum TransportKind
{
	Steam,
	Lan,
}

internal readonly struct NetPacket
{
	public readonly ulong PeerId;
	public readonly byte[] Data;
	public readonly IPEndPoint? LanFrom;

	public NetPacket(ulong peerId, byte[] data, IPEndPoint? lanFrom = null)
	{
		PeerId = peerId;
		Data = data;
		LanFrom = lanFrom;
	}
}

internal interface INetTransport : IDisposable
{
	TransportKind Kind { get; }
	bool IsHost { get; }
	bool Connected { get; }
	string Status { get; }
	ulong LocalPeerId { get; }

	void Pump();
	bool TryReceive(out NetPacket packet);
	void Broadcast(byte[] data, bool reliable);
	void SendTo(ulong peerId, byte[] data, bool reliable);
	void Disconnect();
}
