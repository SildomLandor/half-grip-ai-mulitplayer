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
}

internal static class NetProtocol
{
	public const byte Version = 9;
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

	public static byte[] Pose(ulong ownerId, Vector3 pos, Quaternion rot, float speed, float hp, float time)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Pose);
		w.Write(ownerId);
		w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
		w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
		w.Write(speed);
		w.Write(hp);
		w.Write(time);
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

	public static byte[] Damage(float amount, Vector3 point)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Damage);
		w.Write(amount);
		w.Write(point.x); w.Write(point.y); w.Write(point.z);
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
		int selectedNodeIndex)
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

	public static byte[] GunFx(Vector3 origin, Vector3 target)
	{
		using var ms = new MemoryStream(28);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.GunFx);
		w.Write(origin.x); w.Write(origin.y); w.Write(origin.z);
		w.Write(target.x); w.Write(target.y); w.Write(target.z);
		return ms.ToArray();
	}

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

	/// <summary>Shared run map graph only — not money/inventory.</summary>
	public static byte[] RunSnap(string json)
	{
		using var ms = new MemoryStream(Math.Max(256, (json?.Length ?? 0) + 8));
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RunSnap);
		WriteString(w, json ?? "");
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

	/// <summary>Impulse the receiver should apply to their local player rigidbody.</summary>
	public static byte[] CollideImpulse(Vector3 impulse, Vector3 atPoint)
	{
		using var ms = new MemoryStream(28);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.CollideImpulse);
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

	/// <summary>kind: 0=ammo(compat), 1=health, 2=effect</summary>
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
