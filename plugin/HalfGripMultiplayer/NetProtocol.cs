using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace HalfGripMultiplayer;

internal enum MsgType : byte
{
	Hello = 1,
	Pose = 2,
	Chat = 3,
	Ping = 4,
	Pong = 5,
	Ready = 6,
	Damage = 7,
	Wreck = 8,
	RaceStart = 9,
	RaceAbort = 10,
	LoadScene = 11,
	RaceSetup = 12,
	SlotMap = 13,
	BotPose = 14,
	CarLook = 15,
	RaceEnd = 16,
	Loadout = 17,
	GunFx = 18,
	MapSelect = 19,
	ShopState = 20,
	LapState = 21,
	AmmoFx = 22,
	NitroFx = 23,
	ResetFx = 24,
	BossKill = 25,
	RunSnap = 26,
	DentFx = 27,
	TireFx = 28,
	ShieldFx = 29,
	CollideImpulse = 30,
	CutoffState = 31,
	BlastFx = 32,
	RocketFx = 33,
	PickupFx = 34,
	PeerLeft = 35,
	HornFx = 36,
	WorldPing = 37,
	MoneyFx = 38,
	DestructibleFx = 39,
	JumpFx = 40,
	TrainSnap = 41,
	GearFx = 42,
	TotemFx = 43,
	RunSnapPart = 44,
	SlowMoFx = 45,
	DriftFx = 46,
	SpunOutFx = 47,
	CarDamagedFx = 48,
	AmmoState = 49,
	ExhaustFx = 50,
	MagnetFx = 51,
	GlueFx = 52,
	SpikeFx = 53,
	GlassFx = 54,
	TrailFx = 55,
	TreeFx = 56,
	MudFx = 57,
	TrapFx = 58,
	HammerFx = 59,
	KillFeed = 60,
	LeechFx = 61,
	BurnFx = 62,
	InvertFx = 63,
	ComboFx = 64,
	CheckpointFx = 65,
	GuillotineFx = 66,
	DrainFx = 67,
	TrampleFx = 68,
	ExplodeFx = 69,
	BotFlameFx = 70,
	AmmoEatFx = 71,
	ObjectiveFx = 72,
	TreeKickFx = 73,
	RefillFx = 74,
	ThreatFx = 75,
	CanisterFx = 76,
	CamFx = 77,
	HbLockFx = 78,
	ShortcutFx = 79,
	EarnFx = 80,
	BlinkFx = 81,
	HintFx = 82,
	CabinAmmoFx = 83,
	AimFx = 84,
	MagFx = 85,
	PauseFx = 86,
	CutoffSay = 87,
	GunActFx = 88,
	SmokeFx = 89,
	GlassCrackFx = 90,
	WeatherFx = 91,
	HbGrabFx = 92,
	HealFx = 93,
	ShiftHitFx = 94,
	TrainMoveFx = 95,
	KillAmmoFx = 96,
	FeatFx = 97,
	GunTypeFx = 98,
	ShiftTypeFx = 99,
	ShellFx = 100,
	SlideFx = 101,
	ClipFx = 102,
	UnlockFx = 103,
	CamLookFx = 104,
	RepairFx = 105,
	GrimeFx = 106,
	MagShotFx = 107,
	WindshieldFx = 108,
	ObjSayFx = 109,
	SteerLockFx = 110,
	RecenterFx = 111,
	MatchRevFx = 112,
	RecordFx = 113,
	LapAheadFx = 114,
	NosUpFx = 115,
	PartsFx = 116,
	ShakeFx = 117,
	ChickenFx = 118,
	ChickenGoneFx = 119,
	ItemUseFx = 120,
	ModelFx = 121,
	TornadoSnap = 122,
	BarrelFx = 123,
	DiceFx = 124,
	BeerFx = 125,
}

internal static class NetProtocol
{
	public const byte Version = 40;
	public const int RunSnapChunkBytes = 900;
	public const int DefaultLanPort = 27777;
	public const int SteamChannel = 0;

	public static byte[] Hello(string name, ulong selfId)
	{
		using var ms = new MemoryStream(72);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Hello);
		w.Write(Version);
		w.Write(selfId);
		WriteString(w, name ?? "Player");
		return ms.ToArray();
	}

	/// <summary>v12+: steerDeg + flags. v40+: engineIndicator 0..1.</summary>
	public static byte[] Pose(ulong ownerId, Vector3 pos, Quaternion rot, float speed, float hp, float time,
		float steerDeg = 0f, byte flags = 0, float engineIndicator = -1f)
	{
		using var ms = new MemoryStream(76);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Pose);
		w.Write(ownerId);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
		w.Write(speed);
		w.Write(hp);
		w.Write(time);
		w.Write(steerDeg);
		w.Write(flags);
		w.Write(engineIndicator);
		return ms.ToArray();
	}

	public static byte[] HornFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.HornFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] WorldPing(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.WorldPing);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] MoneyFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MoneyFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] DestructibleFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.DestructibleFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] JumpFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.JumpFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] TrainSnap(byte index, float headDistance, bool moving)
	{
		using var ms = new MemoryStream(12);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TrainSnap);
		w.Write(index);
		w.Write(headDistance);
		w.Write((byte)(moving ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] GearFx(sbyte gear, bool inGear)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GearFx);
		w.Write(gear);
		w.Write((byte)(inGear ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] TotemFx(float healed, int left)
	{
		using var ms = new MemoryStream(12);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TotemFx);
		w.Write(healed);
		w.Write(left);
		return ms.ToArray();
	}

	public static byte[] Chat(string text)
	{
		using var ms = new MemoryStream(128);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Chat);
		WriteString(w, text ?? "");
		return ms.ToArray();
	}

	public static byte[] Ping(uint seq, float clientTime)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Ping);
		w.Write(seq);
		w.Write(clientTime);
		return ms.ToArray();
	}

	public static byte[] Pong(uint seq, float clientTime, float serverTime)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Pong);
		w.Write(seq);
		w.Write(clientTime);
		w.Write(serverTime);
		return ms.ToArray();
	}

	public static byte[] Ready(bool ready)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Ready);
		w.Write((byte)(ready ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] Damage(float amount, Vector3 point, ulong targetPeer = 0)
	{
		using var ms = new MemoryStream(28);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Damage);
		w.Write(amount);
		w.Write(point.x); w.Write(point.y); w.Write(point.z);
		w.Write(targetPeer); // 0 = legacy "receiver is target"
		return ms.ToArray();
	}

	public static byte[] Wreck() => new[] { (byte)MsgType.Wreck };

	public static byte[] RaceStart(float hostTime)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RaceStart);
		w.Write(hostTime);
		return ms.ToArray();
	}

	public static byte[] RaceAbort() => new[] { (byte)MsgType.RaceAbort };

	public static byte[] LoadScene(string sceneName)
	{
		using var ms = new MemoryStream(96);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.LoadScene);
		WriteString(w, sceneName ?? "");
		return ms.ToArray();
	}

	public static byte[] RaceSetup(
		int spawnSeed,
		bool shortRace,
		byte trackVariant,
		int maxLaps,
		string weatherName,
		string mapName,
		string sceneName,
		byte objectiveKind,
		int objectiveAmount,
		string objectiveTarget,
		string musicName,
		float ceasefireSeconds,
		int selectedNodeIndex,
		bool freePlay = false,
		bool noBots = false)
	{
		using var ms = new MemoryStream(384);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RaceSetup);
		w.Write(spawnSeed);
		w.Write((byte)(shortRace ? 1 : 0));
		w.Write(trackVariant);
		w.Write(maxLaps);
		WriteString(w, weatherName ?? "");
		WriteString(w, mapName ?? "");
		WriteString(w, sceneName ?? "");
		w.Write(objectiveKind);
		w.Write(objectiveAmount);
		WriteString(w, objectiveTarget ?? "");
		WriteString(w, musicName ?? "");
		w.Write(ceasefireSeconds);
		w.Write(selectedNodeIndex);
		w.Write((byte)(freePlay ? 1 : 0));
		w.Write((byte)(noBots ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] SlotMap(IReadOnlyList<(ulong peerId, byte slot)> entries)
	{
		using var ms = new MemoryStream(128);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SlotMap);
		w.Write((byte)entries.Count);
		foreach (var (peerId, slot) in entries)
		{
			w.Write(peerId);
			w.Write(slot);
		}
		return ms.ToArray();
	}

	public static byte[] BotPose(IReadOnlyList<(byte index, Vector3 pos, Quaternion rot, float speed, float hp)> bots)
	{
		using var ms = new MemoryStream(32 + bots.Count * 40);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BotPose);
		w.Write((byte)Mathf.Min(bots.Count, 255));
		int n = Mathf.Min(bots.Count, 255);
		for (int i = 0; i < n; i++)
		{
			var b = bots[i];
			w.Write(b.index);
			w.Write(b.pos.x); w.Write(b.pos.y); w.Write(b.pos.z);
			w.Write(b.rot.x); w.Write(b.rot.y); w.Write(b.rot.z); w.Write(b.rot.w);
			w.Write(b.speed);
			w.Write(b.hp);
		}
		return ms.ToArray();
	}

	public static byte[] CarLook(CarLookData look)
	{
		using var ms = new MemoryStream(96);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CarLook);
		w.Write(look.SkinIndex);
		WriteColor(w, look.Color1);
		WriteColor(w, look.Color2);
		WriteColor(w, look.Color3);
		WriteString(w, look.AssetName ?? "");
		WriteString(w, look.ChassisName ?? "");
		return ms.ToArray();
	}

	public static CarLookData ReadCarLook(BinaryReader r) => new()
	{
		SkinIndex = r.ReadByte(),
		Color1 = ReadColor(r),
		Color2 = ReadColor(r),
		Color3 = ReadColor(r),
		AssetName = ReadString(r),
		ChassisName = ReadString(r),
	};

	public static byte[] RaceEnd(int place, bool survived, bool objectiveMet, bool wrecked, bool quit)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RaceEnd);
		w.Write(place);
		w.Write((byte)(survived ? 1 : 0));
		w.Write((byte)(objectiveMet ? 1 : 0));
		w.Write((byte)(wrecked ? 1 : 0));
		w.Write((byte)(quit ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] Loadout(IReadOnlyList<string> upgradeIds)
	{
		using var ms = new MemoryStream(256);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Loadout);
		w.Write((byte)Mathf.Min(upgradeIds.Count, 255));
		int n = Mathf.Min(upgradeIds.Count, 255);
		for (int i = 0; i < n; i++)
			WriteString(w, upgradeIds[i] ?? "");
		return ms.ToArray();
	}

	public static byte[] GunFx(Vector3 origin, Vector3 target, ulong shooterPeer = 0)
	{
		using var ms = new MemoryStream(36);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GunFx);
		w.Write(origin.x); w.Write(origin.y); w.Write(origin.z);
		w.Write(target.x); w.Write(target.y); w.Write(target.z);
		w.Write(shooterPeer);
		return ms.ToArray();
	}

	public static byte[] PeerLeft() => new[] { (byte)MsgType.PeerLeft };

	public static byte[] MapSelect(int selectedIndex, int currentIndex, int races, int lives)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MapSelect);
		w.Write(selectedIndex);
		w.Write(currentIndex);
		w.Write(races);
		w.Write(lives);
		return ms.ToArray();
	}

	public static byte[] ShopState(float money, string offerKey)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShopState);
		w.Write(money);
		WriteString(w, offerKey ?? "");
		return ms.ToArray();
	}

	public static byte[] LapState(int lap, float progress, int place, bool finished)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.LapState);
		w.Write(lap);
		w.Write(progress);
		w.Write(place);
		w.Write((byte)(finished ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] AmmoFx(Vector3 pos, int magazines)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.AmmoFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(magazines);
		return ms.ToArray();
	}

	public static byte[] NitroFx(bool on, float power)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.NitroFx);
		w.Write((byte)(on ? 1 : 0));
		w.Write(power);
		return ms.ToArray();
	}

	public static byte[] ResetFx(Vector3 pos, Quaternion rot)
	{
		using var ms = new MemoryStream(32);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ResetFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
		return ms.ToArray();
	}

	public static byte[] BossKill() => new[] { (byte)MsgType.BossKill };

	/// <summary>Shared run map graph only — not money/inventory. Prefer RunSnapParts for large JSON.</summary>
	public static byte[] RunSnap(string json)
	{
		using var ms = new MemoryStream(Math.Max(256, (json?.Length ?? 0) + 8));
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RunSnap);
		WriteString(w, json ?? "");
		return ms.ToArray();
	}

	/// <summary>MTU-safe RunSnap fragments (id + index/count + payload).</summary>
	public static List<byte[]> RunSnapParts(string json, uint snapId)
	{
		var list = new List<byte[]>();
		var utf8 = Encoding.UTF8.GetBytes(json ?? "");
		int count = Math.Max(1, (utf8.Length + RunSnapChunkBytes - 1) / RunSnapChunkBytes);
		if (count > 255) count = 255;
		for (int i = 0; i < count; i++)
		{
			int offset = i * RunSnapChunkBytes;
			int len = Math.Min(RunSnapChunkBytes, utf8.Length - offset);
			using var ms = new MemoryStream(16 + len);
			using var w = new BinaryWriter(ms);
			w.Write((byte)MsgType.RunSnapPart);
			w.Write(snapId);
			w.Write((byte)i);
			w.Write((byte)count);
			w.Write((ushort)len);
			if (len > 0) w.Write(utf8, offset, len);
			list.Add(ms.ToArray());
		}
		return list;
	}

	public static byte[] SlowMoFx(float duration)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SlowMoFx);
		w.Write(duration);
		return ms.ToArray();
	}

	public static byte[] DriftFx(Vector3 pos, float meters)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.DriftFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(meters);
		return ms.ToArray();
	}

	public static byte[] SpunOutFx(float speed)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SpunOutFx);
		w.Write(speed);
		return ms.ToArray();
	}

	public static byte[] CarDamagedFx(float fraction, byte kind)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CarDamagedFx);
		w.Write(fraction);
		w.Write(kind);
		return ms.ToArray();
	}

	public static byte[] AmmoState(byte magazines)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.AmmoState);
		w.Write(magazines);
		return ms.ToArray();
	}

	public static byte[] ExhaustFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ExhaustFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] MagnetFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MagnetFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] GlueFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GlueFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] SpikeFx(Vector3 point, Vector3 dir)
	{
		using var ms = new MemoryStream(28);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SpikeFx);
		w.Write(point.x); w.Write(point.y); w.Write(point.z);
		w.Write(dir.x); w.Write(dir.y); w.Write(dir.z);
		return ms.ToArray();
	}

	public static byte[] GlassFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GlassFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] TrailFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TrailFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	/// <summary>Host/local tree fell — index into FellableTerrain trunks + lean push.</summary>
	public static byte[] TreeFx(int terrainIndex, int treeIndex, Vector3 push)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TreeFx);
		w.Write((byte)Mathf.Clamp(terrainIndex, 0, 255));
		w.Write(treeIndex);
		w.Write(push.x); w.Write(push.y); w.Write(push.z);
		return ms.ToArray();
	}

	public static byte[] MudFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MudFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] TrapFx(Vector3 pos, float damage)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TrapFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(damage);
		return ms.ToArray();
	}

	public static byte[] HammerFx(Vector3 pos, float damage)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.HammerFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(damage);
		return ms.ToArray();
	}

	public static byte[] KillFeed(string text)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.KillFeed);
		WriteString(w, text ?? "");
		return ms.ToArray();
	}

	public static byte[] LeechFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.LeechFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] BurnFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BurnFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] InvertFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.InvertFx);
		return ms.ToArray();
	}

	public static byte[] ComboFx(string name, float value)
	{
		using var ms = new MemoryStream(48);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ComboFx);
		WriteString(w, name ?? "");
		w.Write(value);
		return ms.ToArray();
	}

	public static byte[] CheckpointFx(byte index, bool starting)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CheckpointFx);
		w.Write(index);
		w.Write((byte)(starting ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] GuillotineFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GuillotineFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] DrainFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.DrainFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] TrampleFx(Vector3 pos, float strength, float radius)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TrampleFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(strength);
		w.Write(radius);
		return ms.ToArray();
	}

	/// <summary>kind: 0=small, 1=big wreck explosion</summary>
	public static byte[] ExplodeFx(byte kind, Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ExplodeFx);
		w.Write(kind);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] BotFlameFx(byte botIndex, bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BotFlameFx);
		w.Write(botIndex);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] AmmoEatFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.AmmoEatFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] ObjectiveFx(string line)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ObjectiveFx);
		WriteString(w, line ?? "");
		return ms.ToArray();
	}

	public static byte[] TreeKickFx(Vector3 pos, Vector3 lean, float degPerSec)
	{
		using var ms = new MemoryStream(32);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TreeKickFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(lean.x); w.Write(lean.y); w.Write(lean.z);
		w.Write(degPerSec);
		return ms.ToArray();
	}

	public static byte[] RefillFx(byte kind, float amount)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RefillFx);
		w.Write(kind);
		w.Write(amount);
		return ms.ToArray();
	}

	public static byte[] ThreatFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ThreatFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] CanisterFx(float charge)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CanisterFx);
		w.Write(charge);
		return ms.ToArray();
	}

	/// <summary>0=interior-ish, 1=outside.</summary>
	public static byte[] CamFx(byte outside)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CamFx);
		w.Write(outside);
		return ms.ToArray();
	}

	public static byte[] HbLockFx(bool locked)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.HbLockFx);
		w.Write((byte)(locked ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] ShortcutFx(float metres)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShortcutFx);
		w.Write(metres);
		return ms.ToArray();
	}

	public static byte[] EarnFx(string label, float amount)
	{
		using var ms = new MemoryStream(48);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.EarnFx);
		WriteString(w, label ?? "");
		w.Write(amount);
		return ms.ToArray();
	}

	public static byte[] BlinkFx(int blinks)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BlinkFx);
		w.Write(blinks);
		return ms.ToArray();
	}

	/// <summary>kind: 0=wrong-way, 1=stuck/off-track</summary>
	public static byte[] HintFx(byte kind, bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.HintFx);
		w.Write(kind);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	/// <summary>picked: 1=picked up, 0=released; magazines count</summary>
	public static byte[] CabinAmmoFx(byte picked, int magazines)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CabinAmmoFx);
		w.Write(picked);
		w.Write(magazines);
		return ms.ToArray();
	}

	public static byte[] AimFx(bool aiming)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.AimFx);
		w.Write((byte)(aiming ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] MagFx(bool seated)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MagFx);
		w.Write((byte)(seated ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] PauseFx(bool paused)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.PauseFx);
		w.Write((byte)(paused ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] CutoffSay(string line)
	{
		using var ms = new MemoryStream(48);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CutoffSay);
		WriteString(w, line ?? "");
		return ms.ToArray();
	}

	/// <summary>kind: 0=shell, 1=breakOpen, 2=breakClose, 3=eject, 4=revSpin</summary>
	public static byte[] GunActFx(byte kind, float param = 0f)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GunActFx);
		w.Write(kind);
		w.Write(param);
		return ms.ToArray();
	}

	public static byte[] SmokeFx(bool on)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SmokeFx);
		w.Write((byte)(on ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] GlassCrackFx(float strength)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GlassCrackFx);
		w.Write(strength);
		return ms.ToArray();
	}

	public static byte[] WeatherFx(string profileName)
	{
		using var ms = new MemoryStream(48);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.WeatherFx);
		WriteString(w, profileName ?? "");
		return ms.ToArray();
	}

	public static byte[] HbGrabFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.HbGrabFx);
		return ms.ToArray();
	}

	public static byte[] HealFx(float amount)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.HealFx);
		w.Write(amount);
		return ms.ToArray();
	}

	/// <summary>kind: 0=money-shift overrev, 1=stall underrev</summary>
	public static byte[] ShiftHitFx(byte kind)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShiftHitFx);
		w.Write(kind);
		return ms.ToArray();
	}

	public static byte[] TrainMoveFx(byte index, bool moving)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TrainMoveFx);
		w.Write(index);
		w.Write((byte)(moving ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] KillAmmoFx(int magazines)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.KillAmmoFx);
		w.Write(magazines);
		return ms.ToArray();
	}

	public static byte[] FeatFx(int featId, string label)
	{
		using var ms = new MemoryStream(48);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.FeatFx);
		w.Write(featId);
		WriteString(w, label ?? "");
		return ms.ToArray();
	}

	public static byte[] GunTypeFx(byte gunType)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GunTypeFx);
		w.Write(gunType);
		return ms.ToArray();
	}

	public static byte[] ShiftTypeFx(byte shifterType)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShiftTypeFx);
		w.Write(shifterType);
		return ms.ToArray();
	}

	/// <summary>grabbed: 1=grab, 0=release</summary>
	public static byte[] ShellFx(byte grabbed)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShellFx);
		w.Write(grabbed);
		return ms.ToArray();
	}

	/// <summary>kind: 0=pull, 1=return, 2=lock, 3=unlock</summary>
	public static byte[] SlideFx(byte kind)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SlideFx);
		w.Write(kind);
		return ms.ToArray();
	}

	public static byte[] ClipFx(int consumed)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ClipFx);
		w.Write(consumed);
		return ms.ToArray();
	}

	public static byte[] UnlockFx(string upgradeId, string displayName)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.UnlockFx);
		WriteString(w, upgradeId ?? "");
		WriteString(w, displayName ?? "");
		return ms.ToArray();
	}

	/// <summary>released: 1=look-around released (exterior free), 0=back to cabin cams</summary>
	public static byte[] CamLookFx(byte released)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CamLookFx);
		w.Write(released);
		return ms.ToArray();
	}

	public static byte[] RepairFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RepairFx);
		return ms.ToArray();
	}

	public static byte[] GrimeFx(float amount)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GrimeFx);
		w.Write(amount);
		return ms.ToArray();
	}

	public static byte[] MagShotFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MagShotFx);
		return ms.ToArray();
	}

	public static byte[] WindshieldFx(bool visible)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.WindshieldFx);
		w.Write((byte)(visible ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] ObjSayFx(string line, float seconds)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ObjSayFx);
		WriteString(w, line ?? "");
		w.Write(seconds);
		return ms.ToArray();
	}

	public static byte[] SteerLockFx(bool locked)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.SteerLockFx);
		w.Write((byte)(locked ? 1 : 0));
		return ms.ToArray();
	}

	public static byte[] RecenterFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RecenterFx);
		return ms.ToArray();
	}

	public static byte[] MatchRevFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.MatchRevFx);
		return ms.ToArray();
	}

	public static byte[] RecordFx(string label, float seconds)
	{
		using var ms = new MemoryStream(48);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RecordFx);
		WriteString(w, label ?? "");
		w.Write(seconds);
		return ms.ToArray();
	}

	public static byte[] LapAheadFx(int lapsAhead)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.LapAheadFx);
		w.Write(lapsAhead);
		return ms.ToArray();
	}

	public static byte[] NosUpFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.NosUpFx);
		return ms.ToArray();
	}

	public static byte[] PartsFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.PartsFx);
		return ms.ToArray();
	}

	public static byte[] ShakeFx(float severity)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShakeFx);
		w.Write(severity);
		return ms.ToArray();
	}

	public static byte[] ChickenFx(int count)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ChickenFx);
		w.Write(count);
		return ms.ToArray();
	}

	public static byte[] ChickenGoneFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ChickenGoneFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	public static byte[] ItemUseFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ItemUseFx);
		return ms.ToArray();
	}

	public static byte[] ModelFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ModelFx);
		return ms.ToArray();
	}

	public static byte[] TornadoSnap(IReadOnlyList<(byte idx, Vector3 pos)> items)
	{
		using var ms = new MemoryStream(4 + items.Count * 13);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TornadoSnap);
		w.Write((byte)Math.Min(items.Count, 255));
		foreach (var (idx, pos) in items)
		{
			w.Write(idx);
			w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		}
		return ms.ToArray();
	}

	public static byte[] BarrelFx(Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BarrelFx);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	/// <summary>kind: 0=hurt, 1=heal, 2=money</summary>
	public static byte[] DiceFx(byte kind)
	{
		using var ms = new MemoryStream(4);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.DiceFx);
		w.Write(kind);
		return ms.ToArray();
	}

	public static byte[] BeerFx()
	{
		using var ms = new MemoryStream(2);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BeerFx);
		return ms.ToArray();
	}

	public static byte[] DentFx(byte botOrPeer, ulong id, Vector3 point, Vector3 normal, float strength, byte kind)
	{
		using var ms = new MemoryStream(40);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.DentFx);
		w.Write(botOrPeer); // 0=peer ghost, 1=bot index as id low byte
		w.Write(id);
		w.Write(point.x); w.Write(point.y); w.Write(point.z);
		w.Write(normal.x); w.Write(normal.y); w.Write(normal.z);
		w.Write(strength);
		w.Write(kind);
		return ms.ToArray();
	}

	public static byte[] TireFx(byte wheelIndex, Vector3 point)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.TireFx);
		w.Write(wheelIndex);
		w.Write(point.x); w.Write(point.y); w.Write(point.z);
		return ms.ToArray();
	}

	public static byte[] ShieldFx(Vector3 point)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.ShieldFx);
		w.Write(point.x); w.Write(point.y); w.Write(point.z);
		return ms.ToArray();
	}

	/// <summary>Impulse the named peer should apply to their local player rigidbody.</summary>
	public static byte[] CollideImpulse(ulong targetPeer, Vector3 impulse, Vector3 atPoint)
	{
		using var ms = new MemoryStream(36);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CollideImpulse);
		w.Write(targetPeer);
		w.Write(impulse.x); w.Write(impulse.y); w.Write(impulse.z);
		w.Write(atPoint.x); w.Write(atPoint.y); w.Write(atPoint.z);
		return ms.ToArray();
	}

	public static byte[] CutoffState(float secondsLeft)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CutoffState);
		w.Write(secondsLeft);
		return ms.ToArray();
	}

	public static byte[] BlastFx(Vector3 centre, float radius, float damageMax, float damageMin, byte source)
	{
		using var ms = new MemoryStream(24);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.BlastFx);
		w.Write(centre.x); w.Write(centre.y); w.Write(centre.z);
		w.Write(radius);
		w.Write(damageMax);
		w.Write(damageMin);
		w.Write(source);
		return ms.ToArray();
	}

	public static byte[] RocketFx(Vector3 origin, Vector3 landing, float speed, float arcHeight)
	{
		using var ms = new MemoryStream(36);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RocketFx);
		w.Write(origin.x); w.Write(origin.y); w.Write(origin.z);
		w.Write(landing.x); w.Write(landing.y); w.Write(landing.z);
		w.Write(speed);
		w.Write(arcHeight);
		return ms.ToArray();
	}

	/// <summary>kind: 0=ammo(compat), 1=health, 2=effect, 3=road pickup</summary>
	public static byte[] PickupFx(byte kind, Vector3 pos)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.PickupFx);
		w.Write(kind);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		return ms.ToArray();
	}

	private static void WriteColor(BinaryWriter w, Color c)
	{
		w.Write(c.r); w.Write(c.g); w.Write(c.b);
	}

	private static Color ReadColor(BinaryReader r) =>
		new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 1f);

	public static void WriteString(BinaryWriter w, string s)
	{
		var bytes = Encoding.UTF8.GetBytes(s ?? "");
		w.Write((ushort)bytes.Length);
		w.Write(bytes);
	}

	public static string ReadString(BinaryReader r)
	{
		var len = r.ReadUInt16();
		return Encoding.UTF8.GetString(r.ReadBytes(len));
	}
}
