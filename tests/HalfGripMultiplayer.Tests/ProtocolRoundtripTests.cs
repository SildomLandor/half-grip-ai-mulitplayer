using System;
using System.IO;
using System.Text;
using Xunit;

namespace HalfGripMultiplayer.Tests;

/// <summary>
/// Pure roundtrip tests mirroring plugin NetProtocol framing (no Unity refs).
/// Keep in sync with plugin/HalfGripMultiplayer/NetProtocol.cs.
/// </summary>
public class ProtocolRoundtripTests
{
	enum MsgType : byte
	{
		Hello = 1, Pose = 2, Chat = 3, Ping = 4, Pong = 5, CopilotInput = 6, Mode = 7, Ready = 8
	}

	const byte Version = 1;

	static byte[] Hello(string name, byte mode)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Hello);
		w.Write(Version);
		w.Write(mode);
		WriteString(w, name);
		return ms.ToArray();
	}

	static byte[] Pose(ulong steamId, float px, float py, float pz, float rx, float ry, float rz, float rw, float speed, float hp, float time)
	{
		using var ms = new MemoryStream(64);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Pose);
		w.Write(steamId);
		w.Write(px); w.Write(py); w.Write(pz);
		w.Write(rx); w.Write(ry); w.Write(rz); w.Write(rw);
		w.Write(speed); w.Write(hp); w.Write(time);
		return ms.ToArray();
	}

	static byte[] Ping(uint seq, float time)
	{
		using var ms = new MemoryStream(16);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Ping);
		w.Write(seq);
		w.Write(time);
		return ms.ToArray();
	}

	static byte[] Pong(uint seq, float clientTime, float serverTime)
	{
		using var ms = new MemoryStream(20);
		using var w = new BinaryWriter(ms);
		w.Write((byte)MsgType.Pong);
		w.Write(seq);
		w.Write(clientTime);
		w.Write(serverTime);
		return ms.ToArray();
	}

	static void WriteString(BinaryWriter w, string s)
	{
		var bytes = Encoding.UTF8.GetBytes(s);
		if (bytes.Length > 200) Array.Resize(ref bytes, 200);
		w.Write((ushort)bytes.Length);
		w.Write(bytes);
	}

	static string ReadString(BinaryReader r)
	{
		var len = r.ReadUInt16();
		return Encoding.UTF8.GetString(r.ReadBytes(len));
	}

	[Fact]
	public void Hello_Roundtrips()
	{
		var bytes = Hello("Pilot", 0);
		using var r = new BinaryReader(new MemoryStream(bytes));
		Assert.Equal(MsgType.Hello, (MsgType)r.ReadByte());
		Assert.Equal(Version, r.ReadByte());
		Assert.Equal((byte)0, r.ReadByte());
		Assert.Equal("Pilot", ReadString(r));
	}

	[Fact]
	public void Pose_Roundtrips()
	{
		var bytes = Pose(99UL, 1.5f, 2.5f, 3.5f, 0f, 0.707f, 0f, 0.707f, 40f, 80f, 12f);
		using var r = new BinaryReader(new MemoryStream(bytes));
		Assert.Equal(MsgType.Pose, (MsgType)r.ReadByte());
		Assert.Equal(99UL, r.ReadUInt64());
		Assert.Equal(1.5f, r.ReadSingle());
		Assert.Equal(2.5f, r.ReadSingle());
		Assert.Equal(3.5f, r.ReadSingle());
		r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
		Assert.Equal(40f, r.ReadSingle());
		Assert.Equal(80f, r.ReadSingle());
		Assert.Equal(12f, r.ReadSingle());
	}

	[Fact]
	public void PingPong_Roundtrips()
	{
		var ping = Ping(7, 1.25f);
		using (var r = new BinaryReader(new MemoryStream(ping)))
		{
			Assert.Equal(MsgType.Ping, (MsgType)r.ReadByte());
			Assert.Equal(7u, r.ReadUInt32());
			Assert.Equal(1.25f, r.ReadSingle());
		}
		var pong = Pong(7, 1.25f, 1.30f);
		using (var r = new BinaryReader(new MemoryStream(pong)))
		{
			Assert.Equal(MsgType.Pong, (MsgType)r.ReadByte());
			Assert.Equal(7u, r.ReadUInt32());
			Assert.Equal(1.25f, r.ReadSingle());
			Assert.Equal(1.30f, r.ReadSingle());
		}
	}
}
