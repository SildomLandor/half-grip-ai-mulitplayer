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
}

internal static class NetProtocol
{
	public const byte Version = 4;
	public const int DefaultLanPort = 27777;
	/// <summary>Facepunch SteamNetworking P2P channel for this mod.</summary>
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

	public static byte[] Wreck()
	{
		return new[] { (byte)MsgType.Wreck };
	}

	public static byte[] RaceStart(float hostTime)
	{
		using var ms = new MemoryStream(8);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RaceStart);
		w.Write(hostTime);
		return ms.ToArray();
	}

	public static byte[] RaceAbort()
	{
		return new[] { (byte)MsgType.RaceAbort };
	}

	public static byte[] LoadScene(string sceneName)
	{
		using var ms = new MemoryStream(96);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.LoadScene);
		WriteString(w, sceneName ?? "");
		return ms.ToArray();
	}

	/// <summary>Host race context: weather, short/laps, variant, spawn seed, scene.</summary>
	public static byte[] RaceSetup(
		int spawnSeed,
		bool shortRace,
		byte trackVariant,
		int maxLaps,
		string weatherName,
		string mapName,
		string sceneName)
	{
		using var ms = new MemoryStream(256);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.RaceSetup);
		w.Write(spawnSeed);
		w.Write((byte)(shortRace ? 1 : 0));
		w.Write(trackVariant);
		w.Write(maxLaps);
		WriteString(w, weatherName ?? "");
		WriteString(w, mapName ?? "");
		WriteString(w, sceneName ?? "");
		return ms.ToArray();
	}

	/// <summary>peerId → grid slot (0 = PlayerPosition, 1+ = enemy grid seats).</summary>
	public static byte[] SlotMap(System.Collections.Generic.IReadOnlyList<(ulong peerId, byte slot)> entries)
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

	public static byte[] BotPose(System.Collections.Generic.IReadOnlyList<(byte index, Vector3 pos, Quaternion rot, float speed, float hp)> bots)
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

	public static void WriteString(BinaryWriter w, string s)
	{
		var bytes = Encoding.UTF8.GetBytes(s ?? "");
		w.Write((ushort)bytes.Length);
		w.Write(bytes);
	}

	public static string ReadString(BinaryReader r)
	{
		var len = r.ReadUInt16();
		var bytes = r.ReadBytes(len);
		return Encoding.UTF8.GetString(bytes);
	}
}
