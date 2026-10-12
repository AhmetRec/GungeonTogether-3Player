using System.IO;
using UnityEngine;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Enums;

namespace GungeonTogether.Networking.Packets
{
    public class PlayerPositionPacket : INetworkPacket
    {
        public PacketType Type => PacketType.PlayerPosition;

        public ulong PlayerId;
        public uint Sequence;
        public float SenderTime;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Rotation;
        public bool IsGrounded;
        public bool IsDodgeRolling;
        public int AnimationState;
        public int SpriteId;
        public string SpriteCollectionName;
        public bool FlipX;
        public string CharacterPrefabKey;
        public int GunId;
        public string GunKey;
        public Vector2 AimDirection;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(Sequence);
            writer.Write(SenderTime);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Velocity.x);
            writer.Write(Velocity.y);
            writer.Write(Rotation);
            writer.Write(IsGrounded);
            writer.Write(IsDodgeRolling);
            writer.Write(AnimationState);
            writer.Write(SpriteId);
            writer.Write(SpriteCollectionName ?? "");
            writer.Write(FlipX);
            writer.Write(CharacterPrefabKey ?? "");
            writer.Write(GunId);
            writer.Write(GunKey ?? "");
            writer.Write(AimDirection.x);
            writer.Write(AimDirection.y);
        }

        public void Deserialize(BinaryReader reader)
        {
            PlayerId = reader.ReadUInt64();
            Sequence = reader.ReadUInt32();
            SenderTime = reader.ReadSingle();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            IsGrounded = reader.ReadBoolean();
            IsDodgeRolling = reader.ReadBoolean();
            AnimationState = reader.ReadInt32();
            SpriteId = reader.ReadInt32();
            SpriteCollectionName = reader.ReadString();
            FlipX = reader.ReadBoolean();
            CharacterPrefabKey = reader.ReadString();
            GunId = reader.ReadInt32();
            GunKey = reader.ReadString();
            AimDirection = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
