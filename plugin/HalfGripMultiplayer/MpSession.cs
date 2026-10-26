using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Steamworks;
using UnityEngine;

namespace HalfGripMultiplayer;

internal sealed class MpSession
{
	public INetTransport? Transport { get; private set; }
	public bool Linked => Transport != null && Transport.Connected;
	public bool IsHost => Transport?.IsHost ?? false;
	public TransportKind Kind => Transport?.Kind ?? TransportKind.Steam;
	public string Status => Transport?.Status ?? "Offline";
	public ulong LocalId => Transport?.LocalPeerId ?? 0;
	public float LastRttMs { get; private set; }
	public bool LocalReady { get; private set; }
	public bool RaceStartReceived { get; set; }
	/// <summary>0 = PlayerPosition (host). Guests get 1, 2, … from host SlotMap.</summary>
	public int LocalSlot { get; private set; }
	public readonly Dictionary<ulong, PeerInfo> Peers = new();
	public readonly Dictionary<ulong, byte> PeerSlots = new();
	public readonly Queue<string> ChatLog = new();

	private uint _pingSeq;
	private float _nextPing;
	private SteamTransport? _steam;
	private LanTransport? _lan;
	private string? _lanJoinHost;
	private int _lanJoinPort;
	private readonly Dictionary<ulong, bool> _pendingReady = new();
	private byte _nextGuestSlot = 1;
	public string? HostSceneName { get; private set; }
	/// <summary>Lobby/prep phase: Ready is manual confirmation, not auto race-start.</summary>
	public bool InLobbyPhase { get; set; } = true;

	public struct PeerInfo
	{
		public string Name;
		public bool Ready;
		public float LastSeen;
		public float Hp;
	}

	public void Pump()
	{
		if (Transport == null) return;
		Transport.Pump();
		while (Transport.TryReceive(out var pkt))
			Handle(pkt);

		if (Linked && Time.unscaledTime >= _nextPing)
		{
			_nextPing = Time.unscaledTime + 2f;
			Broadcast(NetProtocol.Ping(++_pingSeq, Time.unscaledTime), reliable: false);
		}
	}

	public async void HostSteam()
	{
		Leave();
		_steam = new SteamTransport { FriendsOnly = Plugin.Instance.FriendsOnly.Value };
		Transport = _steam;
		if (await _steam.HostAsync())
		{
			LocalSlot = 0;
			PeerSlots[LocalId] = 0;
			InLobbyPhase = true;
			AddChat("* Steam lobby ready — Invite or Shift+Tab");
			Broadcast(NetProtocol.Hello(LocalName(), LocalId), true);
			BroadcastSlotMap();
		}
	}

	public async void JoinSteam(ulong lobbyId)
	{
		Leave();
		_steam = new SteamTransport();
		Transport = _steam;
		if (await _steam.JoinAsync(lobbyId))
		{
			InLobbyPhase = true;
			AddChat("* Joined Steam lobby");
			Broadcast(NetProtocol.Hello(LocalName(), LocalId), true);
		}
	}

	public void HostLan(int port)
	{
		// Dual-client lab: never let a join-* role steal the host UDP port.
		var role = Environment.GetEnvironmentVariable("HGMP_ROLE") ?? "";
		if (role.IndexOf("join", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			Plugin.Log.LogWarning($"HostLan ignored — HGMP_ROLE={role}");
			return;
		}
		Leave();
		_lan = new LanTransport();
		Transport = _lan;
		if (_lan.Host(port))
		{
			LocalSlot = 0;
			PeerSlots[LocalId] = 0;
			InLobbyPhase = true;
			AddChat($"* LAN host on port {port}");
			Broadcast(NetProtocol.Hello(LocalName(), LocalId), true);
			BroadcastSlotMap();
		}
		else
			Plugin.Log.LogWarning($"LAN Host({port}) failed — port busy?");
	}

	public void JoinLan(string host, int port)
	{
		host = (host ?? "").Trim();
		if (port <= 0) port = NetProtocol.DefaultLanPort;
		var role = Environment.GetEnvironmentVariable("HGMP_ROLE") ?? "";
		if (role.IndexOf("host", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			Plugin.Log.LogWarning($"JoinLan ignored — HGMP_ROLE={role}");
			return;
		}
		// Idempotent: auto-role / UI / reconnect spam must not tear down a live socket.
		if (Linked && Kind == TransportKind.Lan && !IsHost
		    && string.Equals(_lanJoinHost, host, StringComparison.OrdinalIgnoreCase)
		    && _lanJoinPort == port)
			return;

		Leave();
		_lan = new LanTransport();
		Transport = _lan;
		_lanJoinHost = host;
		_lanJoinPort = port;
		if (_lan.Join(host, port))
		{
			InLobbyPhase = true;
			AddChat($"* LAN join {host}:{port}");
			Broadcast(NetProtocol.Hello(LocalName(), LocalId), true);
		}
	}

	public void Leave()
	{
		Transport?.Disconnect();
		Transport?.Dispose();
		Transport = null;
		_steam = null;
		_lan = null;
		_lanJoinHost = null;
		_lanJoinPort = 0;
		HostSceneName = null;
		Peers.Clear();
		PeerSlots.Clear();
		_pendingReady.Clear();
		LocalReady = false;
		RaceStartReceived = false;
		LocalSlot = 0;
		_nextGuestSlot = 1;
		InLobbyPhase = true;
	}

	public void BroadcastHostScene(string sceneName)
	{
		if (string.IsNullOrWhiteSpace(sceneName)) return;
		HostSceneName = sceneName;
		if (!IsHost || !Linked) return;
		Broadcast(NetProtocol.LoadScene(sceneName), true);
		Plugin.Log.LogInfo($"Host scene broadcast: {sceneName}");
	}

	public void SendHostSceneTo(ulong peerId)
	{
		if (!IsHost || string.IsNullOrWhiteSpace(HostSceneName)) return;
		SendTo(peerId, NetProtocol.LoadScene(HostSceneName!), true);
	}

	public void BroadcastRaceSetup()
	{
		if (!IsHost || !Linked) return;
		RaceSync.CaptureFromHost();
		// Prefer upcoming track scene name if still in UI.
		var scene = !string.IsNullOrEmpty(RaceSync.SceneName) && RaceSync.SceneName.StartsWith("SCENE_TRACK_", StringComparison.Ordinal)
			? RaceSync.SceneName
			: HostSceneName ?? RaceSync.SceneName;
		Broadcast(NetProtocol.RaceSetup(
			RaceSync.SpawnSeed,
			RaceSync.ShortRace,
			RaceSync.TrackVariantByte,
			RaceSync.MaxLaps,
			RaceSync.WeatherName,
			RaceSync.MapName,
			scene ?? "",
			RaceSync.ObjectiveKindByte,
			RaceSync.ObjectiveAmount,
			RaceSync.ObjectiveTarget,
			RaceSync.MusicName,
			RaceSync.CeasefireSeconds,
			RaceSync.SelectedNodeIndex), true);
		BroadcastSlotMap();
		WorldSync.ForceBroadcastLoadout(this);
		WorldSync.BroadcastMapSelect(this);
		WorldSync.BroadcastRunSnap(this);
		Plugin.Log.LogInfo("RaceSetup broadcast");
	}

	public void BroadcastSlotMap()
	{
		if (!IsHost || !Linked) return;
		EnsureLocalSlot();
		var list = new List<(ulong, byte)>();
		foreach (var kv in PeerSlots)
			list.Add((kv.Key, kv.Value));
		Broadcast(NetProtocol.SlotMap(list), true);
	}

	public void InviteSteam() => _steam?.InviteOverlay();

	public void SetReady(bool ready, bool quiet = false)
	{
		bool changed = LocalReady != ready;
		LocalReady = ready;
		Broadcast(NetProtocol.Ready(ready), true);
		if (!quiet && changed)
			AddChat($"* Local ready={ready}");
	}

	public void ClearAllReady()
	{
		LocalReady = false;
		var ids = new List<ulong>(Peers.Keys);
		foreach (var id in ids)
		{
			var p = Peers[id];
			p.Ready = false;
			Peers[id] = p;
		}
		Broadcast(NetProtocol.Ready(false), true);
	}

	public void BroadcastRaceStart()
	{
		RaceStartReceived = true;
		InLobbyPhase = false;
		Broadcast(NetProtocol.RaceStart(Time.unscaledTime), true);
		AddChat("* Race START");
		Plugin.Log.LogInfo("Race START broadcast (host)");
	}

	public void Broadcast(byte[] data, bool reliable)
	{
		Transport?.Broadcast(data, reliable);
	}

	public void SendTo(ulong peer, byte[] data, bool reliable)
	{
		Transport?.SendTo(peer, data, reliable);
	}

	public void SendChat(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		AddChat($"{LocalName()}: {text.Trim()}");
		Broadcast(NetProtocol.Chat(text.Trim()), true);
	}

	public bool AllPeersReady()
	{
		if (Peers.Count == 0) return LocalReady;
		foreach (var p in Peers.Values)
			if (!p.Ready) return false;
		return LocalReady;
	}

	public int SlotFor(ulong peerId)
	{
		if (PeerSlots.TryGetValue(peerId, out var s)) return s;
		return peerId == LocalId ? LocalSlot : -1;
	}

	private void EnsureLocalSlot()
	{
		if (!PeerSlots.ContainsKey(LocalId))
			PeerSlots[LocalId] = (byte)Mathf.Clamp(LocalSlot, 0, 255);
	}

	private void AssignGuestSlot(ulong peerId)
	{
		if (PeerSlots.ContainsKey(peerId)) return;
		byte slot = _nextGuestSlot;
		if (slot < 255) _nextGuestSlot++;
		PeerSlots[peerId] = slot;
		Plugin.Log.LogInfo($"Assigned peer {peerId} → grid slot {slot}");
	}

	private void Handle(NetPacket pkt)
	{
		try
		{
			using var ms = new MemoryStream(pkt.Data);
			using var r = new BinaryReader(ms);
			var type = (MsgType)r.ReadByte();
			switch (type)
			{
				case MsgType.Hello:
				{
					r.ReadByte(); // ver
					var announcedId = r.ReadUInt64();
					var name = NetProtocol.ReadString(r);
					// LAN: remap temp EP id → guest's self LocalPeerId so SlotMap keys match.
					ulong peerKey = pkt.PeerId;
					if (_lan != null && pkt.LanFrom != null)
					{
						if (announcedId != 0 && announcedId != pkt.PeerId)
						{
							_lan.RemapPeer(pkt.PeerId, announcedId, pkt.LanFrom);
							peerKey = announcedId;
						}
						else
							_lan.RememberPeer(peerKey, pkt.LanFrom);
					}
					bool first = !Peers.ContainsKey(peerKey);
					Peers[peerKey] = new PeerInfo { Name = name, LastSeen = Time.unscaledTime, Hp = 100f };
					if (first)
					{
						AddChat($"* {name} connected");
						Plugin.Log.LogInfo($"Hello from {name} peer={peerKey}");
						if (_pendingReady.TryGetValue(peerKey, out var pending) ||
						    _pendingReady.TryGetValue(pkt.PeerId, out pending))
						{
							var pi = Peers[peerKey];
							pi.Ready = pending;
							Peers[peerKey] = pi;
							_pendingReady.Remove(peerKey);
							_pendingReady.Remove(pkt.PeerId);
						}
						SendTo(peerKey, NetProtocol.Hello(LocalName(), LocalId), true);
						MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
						if (IsHost)
						{
							AssignGuestSlot(peerKey);
							BroadcastSlotMap();
							SendHostSceneTo(peerKey);
							WorldSync.SendLateJoinBundle(this, peerKey);
							if (RaceStartReceived)
								SendTo(peerKey, NetProtocol.RaceStart(Time.unscaledTime), true);
							else if (!InLobbyPhase)
								TryHostStartRace();
						}
					}
					break;
				}
				case MsgType.Pose:
				{
					var id = r.ReadUInt64();
					var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var speed = r.ReadSingle();
					var hp = r.ReadSingle();
					var t = r.ReadSingle();
					Touch(pkt.PeerId, hp);
					MpRuntime.Instance?.ApplyRemotePose(pkt.PeerId, id, pos, rot, speed, hp, t);
					break;
				}
				case MsgType.Chat:
					AddChat($"{PeerName(pkt.PeerId)}: {NetProtocol.ReadString(r)}");
					break;
				case MsgType.Ping:
				{
					var seq = r.ReadUInt32();
					var clientT = r.ReadSingle();
					SendTo(pkt.PeerId, NetProtocol.Pong(seq, clientT, Time.unscaledTime), false);
					break;
				}
				case MsgType.Pong:
				{
					r.ReadUInt32();
					var clientT = r.ReadSingle();
					r.ReadSingle();
					LastRttMs = Mathf.Max(0f, (Time.unscaledTime - clientT) * 1000f);
					Touch(pkt.PeerId);
					break;
				}
				case MsgType.Ready:
				{
					var ready = r.ReadByte() != 0;
					if (Peers.TryGetValue(pkt.PeerId, out var p))
					{
						bool changed = p.Ready != ready;
						p.Ready = ready;
						Peers[pkt.PeerId] = p;
						if (changed)
							AddChat($"* {PeerName(pkt.PeerId)} ready={ready}");
					}
					else
						_pendingReady[pkt.PeerId] = ready;
					if (!InLobbyPhase)
						TryHostStartRace(pkt.PeerId);
					break;
				}
				case MsgType.Damage:
				{
					var amount = r.ReadSingle();
					var point = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					MpRuntime.Instance?.ApplyIncomingDamage(amount, point);
					break;
				}
				case MsgType.Wreck:
					MpRuntime.Instance?.OnPeerWrecked(pkt.PeerId);
					AddChat($"* {PeerName(pkt.PeerId)} wrecked");
					break;
				case MsgType.RaceStart:
					r.ReadSingle();
					RaceStartReceived = true;
					InLobbyPhase = false;
					AddChat("* Race START (synced)");
					Plugin.Log.LogInfo("Race START received from host");
					break;
				case MsgType.RaceAbort:
					RaceStartReceived = false;
					LocalReady = false;
					InLobbyPhase = true;
					break;
				case MsgType.LoadScene:
				{
					var scene = NetProtocol.ReadString(r);
					HostSceneName = scene;
					bool isTrack = scene.StartsWith("SCENE_TRACK_", StringComparison.Ordinal);
					if (!isTrack)
					{
						InLobbyPhase = true;
						RaceStartReceived = false;
						LocalReady = false; // new lobby — everyone must Ready again
						foreach (var id in new List<ulong>(Peers.Keys))
						{
							var pi = Peers[id];
							pi.Ready = false;
							Peers[id] = pi;
						}
					}
					MpRuntime.Instance?.OnHostScene(scene);
					break;
				}
				case MsgType.RaceSetup:
				{
					var seed = r.ReadInt32();
					var shortRace = r.ReadByte() != 0;
					var variant = r.ReadByte();
					var laps = r.ReadInt32();
					var weather = NetProtocol.ReadString(r);
					var map = NetProtocol.ReadString(r);
					var scene = NetProtocol.ReadString(r);
					var objKind = r.ReadByte();
					var objAmount = r.ReadInt32();
					var objTarget = NetProtocol.ReadString(r);
					var music = NetProtocol.ReadString(r);
					var cease = r.ReadSingle();
					var selected = r.ReadInt32();
					RaceSync.ApplyPacket(seed, shortRace, variant, laps, weather, map, scene,
						objKind, objAmount, objTarget, music, cease, selected);
					AddChat($"* Race setup: {map} / {weather} / laps={laps}");
					break;
				}
				case MsgType.SlotMap:
				{
					int count = r.ReadByte();
					PeerSlots.Clear();
					for (int i = 0; i < count; i++)
					{
						var id = r.ReadUInt64();
						var slot = r.ReadByte();
						PeerSlots[id] = slot;
						if (id == LocalId)
							LocalSlot = slot;
					}
					EnsureLocalSlot();
					Plugin.Log.LogInfo($"SlotMap: local slot={LocalSlot}, entries={count}");
					break;
				}
				case MsgType.BotPose:
				{
					int count = r.ReadByte();
					var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
					for (int i = 0; i < count; i++)
					{
						var index = r.ReadByte();
						var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
						var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
						var speed = r.ReadSingle();
						var hp = r.ReadSingle();
						if (!IsHost)
							RaceSync.ApplyBotPose(tm, index, pos, rot, speed, hp);
					}
					break;
				}
				case MsgType.CarLook:
				{
					var look = NetProtocol.ReadCarLook(r);
					MpRuntime.Instance?.ApplyRemoteCarLook(pkt.PeerId, look);
					break;
				}
				case MsgType.RaceEnd:
				{
					var place = r.ReadInt32();
					var survived = r.ReadByte() != 0;
					var objectiveMet = r.ReadByte() != 0;
					var wrecked = r.ReadByte() != 0;
					var quit = r.ReadByte() != 0;
					if (!IsHost)
						WorldSync.ApplyRemoteRaceEnd(place, survived, objectiveMet, wrecked, quit);
					break;
				}
				case MsgType.Loadout:
				{
					int n = r.ReadByte();
					var ids = new List<string>(n);
					for (int i = 0; i < n; i++)
						ids.Add(NetProtocol.ReadString(r));
					WorldSync.StorePeerLoadout(pkt.PeerId, ids);
					break;
				}
				case MsgType.GunFx:
				{
					var origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					WorldSync.SpawnGunTracer(origin, target);
					break;
				}
				case MsgType.MapSelect:
				{
					var selected = r.ReadInt32();
					var current = r.ReadInt32();
					var races = r.ReadInt32();
					var lives = r.ReadInt32();
					if (!IsHost)
						WorldSync.ApplyMapSelect(selected, current, races, lives);
					break;
				}
				case MsgType.ShopState:
				{
					var money = r.ReadSingle();
					var offer = NetProtocol.ReadString(r);
					if (!IsHost)
						WorldSync.ApplyShopState(money, offer);
					break;
				}
				case MsgType.LapState:
				{
					var lap = r.ReadInt32();
					var progress = r.ReadSingle();
					var place = r.ReadInt32();
					var finished = r.ReadByte() != 0;
					WorldSync.StorePeerLap(pkt.PeerId, lap, progress, place, finished);
					break;
				}
				case MsgType.AmmoFx:
				{
					var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var mags = r.ReadInt32();
					WorldSync.ApplyAmmoFx(pos, mags);
					break;
				}
				case MsgType.NitroFx:
				{
					var on = r.ReadByte() != 0;
					var power = r.ReadSingle();
					WorldSync.ApplyNitroFx(pkt.PeerId, on, power);
					break;
				}
				case MsgType.ResetFx:
				{
					var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					WorldSync.ApplyResetFx(pkt.PeerId, pos, rot);
					break;
				}
				case MsgType.BossKill:
					if (!IsHost)
						WorldSync.ApplyBossKill();
					break;
				case MsgType.RunSnap:
				{
					var json = NetProtocol.ReadString(r);
					if (!IsHost)
						WorldSync.ApplyRunSnap(json);
					break;
				}
				case MsgType.DentFx:
				{
					var mode = r.ReadByte();
					var id = r.ReadUInt64();
					var point = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var normal = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
					var strength = r.ReadSingle();
					var kind = r.ReadByte();
					WorldSync.ApplyDentFx(mode, id, point, normal, strength, kind);
					break;
				}
				default:
					break;
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"Packet error: {ex.Message}");
		}
	}

	private void TryHostStartRace(ulong? fromPeer = null)
	{
		if (!IsHost) return;
		if (RaceStartReceived)
		{
			if (fromPeer.HasValue)
				SendTo(fromPeer.Value, NetProtocol.RaceStart(Time.unscaledTime), true);
			return;
		}
		if (InLobbyPhase) return; // lobby Ready does not start the countdown
		if (!LocalReady) return;
		if (Peers.Count == 0) return;
		if (!AllPeersReady()) return;
		BroadcastRaceStart();
	}

	private void Touch(ulong id, float? hp = null)
	{
		if (Peers.TryGetValue(id, out var p))
		{
			p.LastSeen = Time.unscaledTime;
			if (hp.HasValue) p.Hp = hp.Value;
			Peers[id] = p;
		}
		else
		{
			Peers[id] = new PeerInfo { Name = id.ToString(), LastSeen = Time.unscaledTime, Hp = hp ?? 100f };
		}
	}

	private string PeerName(ulong id) => Peers.TryGetValue(id, out var p) ? p.Name : id.ToString();

	public void AddChat(string line)
	{
		ChatLog.Enqueue(line);
		while (ChatLog.Count > 50) ChatLog.Dequeue();
	}

	private static string LocalName()
	{
		try
		{
			if (SteamIdentity.Ready && !string.IsNullOrWhiteSpace(SteamIdentity.LocalName))
				return SteamIdentity.LocalName;
		}
		catch { /* offline LAN */ }
		var env = Environment.GetEnvironmentVariable("HGMP_NAME");
		if (!string.IsNullOrWhiteSpace(env)) return env!;
		return "Driver" + (Environment.TickCount % 1000);
	}
}
