using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace HalfGripMultiplayer;

internal sealed class SteamTransport : INetTransport
{
	private Lobby? _lobby;
	private readonly Queue<NetPacket> _inbox = new();
	private bool _hooks;

	public TransportKind Kind => TransportKind.Steam;
	public bool IsHost { get; private set; }
	public bool Connected => _lobby.HasValue;
	public string Status { get; private set; } = "Steam idle";
	public ulong LocalPeerId => SteamClient.IsValid ? (ulong)SteamClient.SteamId : 1UL;
	public Lobby? Lobby => _lobby;
	public bool FriendsOnly { get; set; } = true;

	public async Task<bool> HostAsync(int maxMembers = 4)
	{
		EnsureSteam();
		EnsureHooks();
		Disconnect();
		Status = "Creating Steam lobby…";
		var lobby = await SteamMatchmaking.CreateLobbyAsync(maxMembers);
		if (!lobby.HasValue)
		{
			Status = "Steam lobby create failed";
			return false;
		}
		var L = lobby.Value;
		if (FriendsOnly) L.SetFriendsOnly();
		else L.SetPrivate();
		L.SetJoinable(true);
		L.SetData("hgmp", PluginInfo.Version);
		L.SetData("pvp", "1");
		L.MaxMembers = maxMembers;
		_lobby = L;
		IsHost = true;
		Status = $"Steam host {L.Id}";
		Plugin.Log.LogInfo(Status);
		return true;
	}

	public async Task<bool> JoinAsync(SteamId lobbyId)
	{
		EnsureSteam();
		EnsureHooks();
		Disconnect();
		Status = $"Joining Steam {lobbyId}…";
		var lobby = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
		if (!lobby.HasValue)
		{
			Status = "Steam join failed";
			return false;
		}
		_lobby = lobby;
		IsHost = lobby.Value.IsOwnedBy(SteamClient.SteamId);
		Status = $"Steam joined {lobby.Value.Id}";
		Plugin.Log.LogInfo(Status);
		return true;
	}

	public void InviteOverlay()
	{
		if (_lobby.HasValue)
			SteamFriends.OpenGameInviteOverlay(_lobby.Value.Id);
	}

	public void Pump()
	{
		if (!SteamClient.IsValid) return;
		SteamClient.RunCallbacks();
		while (SteamNetworking.IsP2PPacketAvailable(NetProtocol.SteamChannel))
		{
			var pkt = SteamNetworking.ReadP2PPacket(NetProtocol.SteamChannel);
			if (pkt == null || pkt.Value.Data == null || pkt.Value.Data.Length == 0) continue;
			_inbox.Enqueue(new NetPacket(pkt.Value.SteamId, pkt.Value.Data));
		}
	}

	public bool TryReceive(out NetPacket packet)
	{
		if (_inbox.Count > 0)
		{
			packet = _inbox.Dequeue();
			return true;
		}
		packet = default;
		return false;
	}

	public void Broadcast(byte[] data, bool reliable)
	{
		if (!_lobby.HasValue) return;
		var send = reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay;
		foreach (var m in _lobby.Value.Members)
		{
			if (m.Id == SteamClient.SteamId) continue;
			SteamNetworking.SendP2PPacket(m.Id, data, data.Length, NetProtocol.SteamChannel, send);
		}
	}

	public void SendTo(ulong peerId, byte[] data, bool reliable)
	{
		var send = reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay;
		SteamNetworking.SendP2PPacket(peerId, data, data.Length, NetProtocol.SteamChannel, send);
	}

	public void Disconnect()
	{
		if (_lobby.HasValue)
		{
			try { _lobby.Value.Leave(); } catch { /* ignore */ }
		}
		_lobby = null;
		IsHost = false;
		Status = "Steam idle";
	}

	public void Dispose() => Disconnect();

	private void EnsureHooks()
	{
		if (_hooks) return;
		_hooks = true;
		SteamNetworking.AllowP2PPacketRelay(true);
		SteamNetworking.OnP2PSessionRequest = id => SteamNetworking.AcceptP2PSessionWithUser(id);
		SteamFriends.OnGameLobbyJoinRequested += async (lobby, _) => await JoinAsync(lobby.Id);
	}

	private static void EnsureSteam()
	{
		if (SteamClient.IsValid) return;
		SteamClient.Init(4163360u, asyncCallbacks: false);
	}
}
