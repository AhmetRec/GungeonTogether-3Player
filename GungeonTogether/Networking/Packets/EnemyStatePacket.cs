using System.IO;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Interfaces;
using UnityEngine;

namespace GungeonTogether.Networking.Packets
{
    public class EnemyStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyState;

        public int EnemyId;
        public Vector2 Position;
        public float Rotation;
        public float Health;
        public int AIState;
        public Vector2 Velocity;
        public int SpriteId;
        public bool FlipX;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Rotation);
            writer.Write(Health);
            writer.Write(AIState);
            writer.Write(Velocity.x);
            writer.Write(Velocity.y);
            writer.Write(SpriteId);
            writer.Write(FlipX);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            Health = reader.ReadSingle();
            AIState = reader.ReadInt32();
            Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            SpriteId = reader.ReadInt32();
            FlipX = reader.ReadBoolean();
        }
    }
}
