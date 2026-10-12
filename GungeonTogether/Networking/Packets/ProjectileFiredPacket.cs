using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class ProjectileFiredPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ProjectileFired;
        public ulong PlayerId;
        public int GunId;
        public string GunKey;
        public string ProjectileName;
        public Vector2 Position;
        public Vector2 Direction;
        public float Speed;
        public float Damage;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(GunId);
            writer.Write(GunKey ?? "");
            writer.Write(ProjectileName ?? "");
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Direction.x);
            writer.Write(Direction.y);
            writer.Write(Speed);
            writer.Write(Damage);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            GunId = reader.ReadInt32();
            GunKey = reader.ReadString();
            ProjectileName = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Speed = reader.ReadSingle();
            Damage = reader.ReadSingle();
        }
    }
}
