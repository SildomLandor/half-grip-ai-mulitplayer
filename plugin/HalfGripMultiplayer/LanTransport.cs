using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace HalfGripMultiplayer;

/// <summary>
/// UDP LAN transport with seq/ack reliable channel.
/// Frame: [0xFE][uint seq][payload…]  Ack: [0xFD][uint seq]
/// </summary>
internal sealed class LanTransport : INetTransport
{
	private const byte ReliableMagic = 0xFE;
	private const byte AckMagic = 0xFD;
	private const float ResendInterval = 0.12f;
	private const float ReliableTtl = 8f;

	private UdpClient? _udp;
	private readonly Queue<NetPacket> _inbox = new();
	private readonly object _lock = new();
	private readonly Dictionary<ulong, IPEndPoint> _peers = new();
	private readonly Dictionary<uint, PendingReliable> _pending = new();
	private readonly HashSet<ulong> _seenReliable = new(); // (peer<<32)|seq key truncated
	private IPEndPoint? _hostEp;
	private Thread? _recvThread;
	private volatile bool _running;
	private ulong _nextGuestId = 2;
	private uint _nextSeq = 1;

	private struct PendingReliable
	{
		public byte[] Frame;
		public IPEndPoint Ep;
		public float NextSend;
		public float ExpireAt;
	}

	public TransportKind Kind => TransportKind.Lan;
	public bool IsHost { get; private set; }
	public bool Connected { get; private set; }
	public string Status { get; private set; } = "LAN idle";
	public ulong LocalPeerId { get; private set; } = 1;
	public int Port { get; private set; } = NetProtocol.DefaultLanPort;

	public bool Host(int port)
	{
		Disconnect();
		Port = port;
		LocalPeerId = 1;
		IsHost = true;
		_udp = new UdpClient(port);
		_udp.Client.ReceiveTimeout = 200;
		Connected = true;
		Status = $"LAN host :{port}";
		StartRecv();
		Plugin.Log.LogInfo(Status);
		return true;
	}

	public bool Join(string host, int port)
	{
		Disconnect();
		Port = port;
		LocalPeerId = ((ulong)(uint)Environment.TickCount) | 0x100000000UL;
		IsHost = false;
		_hostEp = new IPEndPoint(IPAddress.Parse(host), port);
		_udp = new UdpClient(0);
		_udp.Client.ReceiveTimeout = 200;
		_peers[1] = _hostEp;
		Connected = true;
		Status = $"LAN join {host}:{port}";
		StartRecv();
		SendRaw(_hostEp, NetProtocol.Hello("lan-" + LocalPeerId, LocalPeerId));
		Plugin.Log.LogInfo(Status);
		return true;
	}

	public void Pump()
	{
		float now = Time.unscaledTime;
		List<uint>? expired = null;
		lock (_lock)
		{
			foreach (var kv in _pending)
			{
				var p = kv.Value;
				if (now >= p.ExpireAt)
				{
					expired ??= new List<uint>();
					expired.Add(kv.Key);
					continue;
				}
				if (now >= p.NextSend)
				{
					SendRaw(p.Ep, p.Frame);
					p.NextSend = now + ResendInterval;
					_pending[kv.Key] = p;
				}
			}
			if (expired != null)
			{
				foreach (var s in expired)
					_pending.Remove(s);
			}
		}
	}

	public bool TryReceive(out NetPacket packet)
	{
		lock (_lock)
		{
			if (_inbox.Count > 0)
			{
				packet = _inbox.Dequeue();
				return true;
			}
		}
		packet = default;
		return false;
	}

	public void Broadcast(byte[] data, bool reliable)
	{
		if (_udp == null) return;
		if (IsHost)
		{
			foreach (var kv in _peers)
				SendInternal(kv.Value, data, reliable);
		}
		else if (_hostEp != null)
		{
			SendInternal(_hostEp, data, reliable);
		}
	}

	public void SendTo(ulong peerId, byte[] data, bool reliable)
	{
		if (_udp == null) return;
		if (_peers.TryGetValue(peerId, out var ep))
			SendInternal(ep, data, reliable);
		else if (!IsHost && _hostEp != null)
			SendInternal(_hostEp, data, reliable);
	}

	private void SendInternal(IPEndPoint ep, byte[] data, bool reliable)
	{
		if (!reliable)
		{
			SendRaw(ep, data);
			return;
		}
		uint seq = _nextSeq++;
		if (_nextSeq == 0) _nextSeq = 1;
		var frame = new byte[5 + data.Length];
		frame[0] = ReliableMagic;
		frame[1] = (byte)(seq);
		frame[2] = (byte)(seq >> 8);
		frame[3] = (byte)(seq >> 16);
		frame[4] = (byte)(seq >> 24);
		Buffer.BlockCopy(data, 0, frame, 5, data.Length);
		float now = Time.unscaledTime;
		lock (_lock)
		{
			_pending[seq] = new PendingReliable
			{
				Frame = frame,
				Ep = ep,
				NextSend = now + ResendInterval,
				ExpireAt = now + ReliableTtl,
			};
		}
		SendRaw(ep, frame);
	}

	public void Disconnect()
	{
		_running = false;
		try { _udp?.Close(); } catch { /* ignore */ }
		_udp = null;
		_peers.Clear();
		_hostEp = null;
		Connected = false;
		IsHost = false;
		Status = "LAN idle";
		lock (_lock)
		{
			_inbox.Clear();
			_pending.Clear();
			_seenReliable.Clear();
		}
	}

	public void Dispose() => Disconnect();

	public void RememberPeer(ulong id, IPEndPoint ep) => _peers[id] = ep;

	public void RemapPeer(ulong fromId, ulong toId, IPEndPoint ep)
	{
		if (fromId != toId)
			_peers.Remove(fromId);
		_peers[toId] = ep;
	}

	public ulong RegisterGuest(IPEndPoint ep)
	{
		foreach (var kv in _peers)
		{
			if (kv.Value.Equals(ep)) return kv.Key;
		}
		var id = _nextGuestId++;
		_peers[id] = ep;
		return id;
	}

	private void StartRecv()
	{
		_running = true;
		_recvThread = new Thread(RecvLoop) { IsBackground = true, Name = "HGMP-LAN" };
		_recvThread.Start();
	}

	private void RecvLoop()
	{
		while (_running && _udp != null)
		{
			try
			{
				IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
				var data = _udp.Receive(ref remote);
				if (data == null || data.Length == 0) continue;
				ulong peerId = IsHost ? RegisterGuest(remote) : 1UL;

				if (data.Length >= 5 && data[0] == AckMagic)
				{
					uint seq = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
					lock (_lock) _pending.Remove(seq);
					continue;
				}

				if (data.Length >= 5 && data[0] == ReliableMagic)
				{
					uint seq = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
					SendRaw(remote, BuildAck(seq));
					ulong seenKey = (peerId << 32) ^ seq;
					bool dup;
					lock (_lock)
					{
						dup = !_seenReliable.Add(seenKey);
						if (_seenReliable.Count > 4096)
							_seenReliable.Clear();
					}
					if (dup) continue;
					var payload = new byte[data.Length - 5];
					Buffer.BlockCopy(data, 5, payload, 0, payload.Length);
					lock (_lock)
						_inbox.Enqueue(new NetPacket(peerId, payload, remote));
					continue;
				}

				lock (_lock)
					_inbox.Enqueue(new NetPacket(peerId, data, remote));
			}
			catch (SocketException) { /* timeout */ }
			catch (ObjectDisposedException) { break; }
			catch (Exception ex) { Plugin.Log.LogWarning($"LAN recv: {ex.Message}"); }
		}
	}

	private static byte[] BuildAck(uint seq) =>
		new[] { AckMagic, (byte)seq, (byte)(seq >> 8), (byte)(seq >> 16), (byte)(seq >> 24) };

	private void SendRaw(IPEndPoint ep, byte[] data)
	{
		try { _udp?.Send(data, data.Length, ep); }
		catch (Exception ex) { Plugin.Log.LogWarning($"LAN send: {ex.Message}"); }
	}
}
