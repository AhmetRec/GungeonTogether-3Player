using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;

namespace GungeonTogether.Networking.Packets
{
    public class EnemyProjectileSpawnPacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyProjectileSpawn;
        public int ProjectileId;
        public int EnemyId;
        public string BulletName;
        public Vector2 Position;
        public Vector2 Direction;
        public float Speed;
        public float Damage;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(ProjectileId);
            writer.Write(EnemyId);
            writer.Write(BulletName ?? "");
            writer.Write(Position.x); writer.Write(Position.y);
            writer.Write(Direction.x); writer.Write(Direction.y);
            writer.Write(Speed); writer.Write(Damage);
        }

        public void Deserialize(BinaryReader reader)
        {
            ProjectileId = reader.ReadInt32();
            EnemyId = reader.ReadInt32();
            BulletName = reader.ReadString();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Speed = reader.ReadSingle(); Damage = reader.ReadSingle();
        }
    }

    public class EnemyProjectileStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyProjectileState;
        public int ProjectileId;
        public Vector2 Position;
        public Vector2 Direction;
        public float Speed;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(ProjectileId);
            writer.Write(Position.x); writer.Write(Position.y);
            writer.Write(Direction.x); writer.Write(Direction.y);
            writer.Write(Speed);
        }

        public void Deserialize(BinaryReader reader)
        {
            ProjectileId = reader.ReadInt32();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Direction = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Speed = reader.ReadSingle();
        }
    }

    public class EnemyProjectileDestroyPacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyProjectileDestroy;
        public int ProjectileId;
        public void Serialize(BinaryWriter writer) => writer.Write(ProjectileId);
        public void Deserialize(BinaryReader reader) => ProjectileId = reader.ReadInt32();
    }
}
