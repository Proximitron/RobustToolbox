using System;
using System.Collections.Immutable;
using Lidgren.Network;
using Robust.Shared.Serialization;

#nullable disable

namespace Robust.Shared.Network.Messages.Handshake
{
    internal sealed class MsgLoginStart : NetMessage
    {
        // **NOTE**: This is a special message sent during the client<->server handshake.
        // It doesn't actually get sent normally and as such doesn't have the "normal" boilerplate.
        // It's basically just a sane way to encapsulate the message write/read logic.
        public override string MsgName => string.Empty;

        public override MsgGroups MsgGroup => MsgGroups.Core;

        public string UserName;
        public bool CanAuth;
        public bool NeedPubKey;
        public bool Encrypt;
        #region Starlight
        public bool Discord;
        public bool Steam;

        /// <summary>
        /// Version of the Starlight client engine, written by every Starlight build.
        /// 0 = official RT client (no Starlight data in the message),
        /// 1 = legacy Starlight client (Discord/Steam flags only, no explicit version).
        /// </summary>
        public byte StarlightClientVersion;

        public const byte CurrentStarlightClientVersion = 2;
        #endregion

        public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
        {
            UserName = buffer.ReadString();
            CanAuth = buffer.ReadBoolean();
            NeedPubKey = buffer.ReadBoolean();
            Encrypt = buffer.ReadBoolean();

            // Starlight-start
            var hasStarlightData = buffer.Position < buffer.LengthBits;
            Discord = hasStarlightData && buffer.ReadBoolean();
            Steam = buffer.Position < buffer.LengthBits && buffer.ReadBoolean();
            if (buffer.LengthBits - buffer.Position >= 8)
                StarlightClientVersion = Math.Max(buffer.ReadByte(), (byte) 1);
            else
                StarlightClientVersion = hasStarlightData ? (byte) 1 : (byte) 0;
            // Starlight-end
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
        {
            buffer.Write(UserName);
            buffer.Write(CanAuth);
            buffer.Write(NeedPubKey);
            buffer.Write(Encrypt);

            // Starlight-start
            buffer.Write(Discord);
            buffer.Write(Steam);
            buffer.Write(CurrentStarlightClientVersion);
            // Starlight-end
        }
    }
}
