using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HalfGripMultiplayer;

/// <summary>Simple UDP LAN/localhost transport for dual-client testing and LAN play.</summary>
internal sealed class LanTransport : INetTransport
{
	private UdpClient? _udp;
	private readonly Queue<NetPacket> _inbox = new();
	private readonly object _lock = new();
	private readonly Dictionary<ulong, IPEndPoint> _peers = new();
	private IPEndPoint? _hostEp;
	private Thread? _recvThread;
	private volatile bool _running;
	private ulong _nextGuestId = 2;

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
		// Announce so host learns our endpoint / id
		SendRaw(_hostEp, NetProtocol.Hello("lan-" + LocalPeerId));
		Plugin.Log.LogInfo(Status);
		return true;
	}

	public void Pump()
	{
		// receive thread fills inbox
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
		_ = reliable;
		if (_udp == null) return;
		if (IsHost)
		{
			foreach (var kv in _peers)
				SendRaw(kv.Value, data);
		}
		else if (_hostEp != null)
		{
			SendRaw(_hostEp, data);
		}
	}

	public void SendTo(ulong peerId, byte[] data, bool reliable)
	{
		_ = reliable;
		if (_udp == null) return;
		if (_peers.TryGetValue(peerId, out var ep))
			SendRaw(ep, data);
		else if (!IsHost && _hostEp != null)
			SendRaw(_hostEp, data);
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
		lock (_lock) _inbox.Clear();
	}

	public void Dispose() => Disconnect();

	public void RememberPeer(ulong id, IPEndPoint ep)
	{
		_peers[id] = ep;
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
				ulong peerId;
				if (IsHost)
					peerId = RegisterGuest(remote);
				else
					peerId = 1;
				lock (_lock)
					_inbox.Enqueue(new NetPacket(peerId, data, remote));
			}
			catch (SocketException)
			{
				// timeout
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"LAN recv: {ex.Message}");
			}
		}
	}

	private void SendRaw(IPEndPoint ep, byte[] data)
	{
		try { _udp?.Send(data, data.Length, ep); }
		catch (Exception ex) { Plugin.Log.LogWarning($"LAN send: {ex.Message}"); }
	}
}
